using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Inngest.IntegrationTests.Evaluation.Harness;
using Inngest.IntegrationTests.Harness;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Inngest.IntegrationTests.Webhooks;

// MapInngestWebhook against the dev server: the endpoint of a second app, on a real Kestrel server, sends the events to
// the dev server, and the functions of the fixture's app run for them. What the tests prove is that a webhook the
// provider delivers again, with the id the provider gave it, starts one run, because Inngest drops the second event.
public sealed class WebhookTests(EvaluationEnvironment environment) : EvaluationScenario<WebhooksFixture>(environment)
{
    private const string StripeSecret = "whsec_Hk3pX9wZq2LmN7vR4tYb8cDf6gJa1sE5";
    private const string GitHubSecret = "It's a Secret to Everybody";

    // How long to keep watching after the last run that must start, for one that must not.
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(2);

    [Fact(Timeout = 90_000)]
    public async Task A_stripe_webhook_the_provider_delivers_again_starts_one_run()
    {
        await using var endpoint = await StartEndpointAsync(WebhookProvider.Stripe(StripeSecret));
        var invoice = $"evt_{TestId}_a";

        // Stripe signs every attempt again, with a new timestamp, and sends the same event.
        (await PostStripeAsync(endpoint, invoice, secondsAgo: 30)).ShouldBe(HttpStatusCode.OK);
        await WaitForObservationsAsync(WebhookFunctions.Delivery);
        (await PostStripeAsync(endpoint, invoice, secondsAgo: 0)).ShouldBe(HttpStatusCode.OK);
        (await PostStripeAsync(endpoint, invoice, secondsAgo: 0)).ShouldBe(HttpStatusCode.OK);

        // An event with another id is the sentinel: once its run has started, a run for a duplicate would have too.
        (await PostStripeAsync(endpoint, $"evt_{TestId}_b", secondsAgo: 0)).ShouldBe(HttpStatusCode.OK);
        await WaitForObservationsAsync(WebhookFunctions.Delivery, count: 2);
        await Task.Delay(Grace, Cancellation);

        Observed.Values<string>(WebhookFunctions.Delivery).ShouldBe([invoice, $"evt_{TestId}_b"], ignoreOrder: true);
    }

    [Fact(Timeout = 90_000)]
    public async Task Without_an_id_the_same_webhook_starts_a_run_every_time_which_is_what_the_id_prevents()
    {
        await using var endpoint = await StartEndpointAsync(
            WebhookProvider.Stripe(StripeSecret),
            new WebhookOptions { Transform = (_, @event) => @event with { Id = null } });
        var invoice = $"evt_{TestId}_a";

        (await PostStripeAsync(endpoint, invoice, secondsAgo: 0)).ShouldBe(HttpStatusCode.OK);
        (await PostStripeAsync(endpoint, invoice, secondsAgo: 0)).ShouldBe(HttpStatusCode.OK);

        var runs = await WaitForObservationsAsync(WebhookFunctions.Delivery, count: 2);
        runs.Select(run => (string)run.Value!).ShouldBe([invoice, invoice]);
    }

    [Fact(Timeout = 90_000)]
    public async Task A_github_delivery_is_deduplicated_by_the_header_that_names_it()
    {
        await using var endpoint = await StartEndpointAsync(WebhookProvider.GitHub(GitHubSecret));
        var delivery = $"72d3162e-cc78-11e3-81ab-{TestId[..12]}";

        (await PostGitHubAsync(endpoint, delivery)).ShouldBe(HttpStatusCode.OK);
        await WaitForObservationsAsync(WebhookFunctions.Delivery);
        (await PostGitHubAsync(endpoint, delivery)).ShouldBe(HttpStatusCode.OK);
        (await PostGitHubAsync(endpoint, $"{delivery}-next")).ShouldBe(HttpStatusCode.OK);
        await WaitForObservationsAsync(WebhookFunctions.Delivery, count: 2);
        await Task.Delay(Grace, Cancellation);

        Observed.Values<string>(WebhookFunctions.Delivery).Count.ShouldBe(2);
    }

    [Fact(Timeout = 90_000)]
    public async Task A_request_with_a_wrong_signature_or_an_old_timestamp_starts_no_run_and_says_nothing()
    {
        await using var endpoint = await StartEndpointAsync(WebhookProvider.Stripe(StripeSecret));

        var forged = await PostStripeAsync(endpoint, $"evt_{TestId}_forged", secondsAgo: 0, secret: "whsec_SomebodyElsesSecret0000000000000000");
        var replayed = await PostStripeAsync(endpoint, $"evt_{TestId}_replayed", secondsAgo: 3600);
        (await PostStripeAsync(endpoint, $"evt_{TestId}_real", secondsAgo: 0)).ShouldBe(HttpStatusCode.OK);
        await WaitForObservationsAsync(WebhookFunctions.Delivery);
        await Task.Delay(Grace, Cancellation);

        forged.ShouldBe(HttpStatusCode.Unauthorized);
        replayed.ShouldBe(HttpStatusCode.Unauthorized);
        Observed.Values<string>(WebhookFunctions.Delivery).ShouldBe([$"evt_{TestId}_real"]);
    }

    [Fact(Timeout = 90_000)]
    public async Task A_dropped_delivery_is_answered_with_a_204_and_no_body()
    {
        await using var endpoint = await StartEndpointAsync(
            WebhookProvider.Stripe(StripeSecret),
            new WebhookOptions { Transform = (_, @event) => null });

        using var response = await endpoint.Http.SendAsync(StripeRequest($"evt_{TestId}_a", secondsAgo: 0, StripeSecret), Cancellation);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await response.Content.ReadAsStringAsync(Cancellation)).ShouldBeEmpty();
        Observed.Named(WebhookFunctions.Delivery).ShouldBeEmpty();
    }

    [Fact(Timeout = 90_000)]
    public async Task An_accepted_webhook_is_answered_with_an_empty_200()
    {
        await using var endpoint = await StartEndpointAsync(WebhookProvider.Stripe(StripeSecret));

        using var response = await endpoint.Http.SendAsync(StripeRequest($"evt_{TestId}_a", secondsAgo: 0, StripeSecret), Cancellation);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Cancellation)).ShouldBeEmpty();
        await WaitForObservationsAsync(WebhookFunctions.Delivery);
    }

    private Task<HttpStatusCode> PostStripeAsync(Endpoint endpoint, string eventId, int secondsAgo, string secret = StripeSecret) =>
        SendAsync(endpoint, StripeRequest(eventId, secondsAgo, secret));

    private async Task<HttpStatusCode> PostGitHubAsync(Endpoint endpoint, string delivery)
    {
        var body = $$$"""{"ref":"refs/heads/main","repository":{"full_name":"{{{TestId}}}"}}""";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhook") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-GitHub-Event", "push");
        request.Headers.Add("X-GitHub-Delivery", delivery);
        request.Headers.Add("X-Hub-Signature-256", "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(GitHubSecret), Encoding.UTF8.GetBytes(body))));
        using var response = await endpoint.Http.SendAsync(request, Cancellation);
        return response.StatusCode;
    }

    private async Task<HttpStatusCode> SendAsync(Endpoint endpoint, HttpRequestMessage request)
    {
        using (request)
        {
            using var response = await endpoint.Http.SendAsync(request, Cancellation);
            return response.StatusCode;
        }
    }

    // An invoice.paid event that Stripe would send, signed at the given time with the given secret.
    private HttpRequestMessage StripeRequest(string eventId, int secondsAgo, string secret)
    {
        var body = JsonSerializer.Serialize(new
        {
            id = eventId,
            @object = "event",
            type = "invoice.paid",
            data = new { @object = new { id = $"in_{TestId[..8]}", @object = "invoice", metadata = new { testId = TestId } } },
        });
        var timestamp = DateTimeOffset.UtcNow.AddSeconds(-secondsAgo).ToUnixTimeSeconds();
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}")));
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhook") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("Stripe-Signature", $"t={timestamp},v1={signature}");
        return request;
    }

    // The webhook endpoint of an app of its own, on Kestrel, whose client sends to the fixture's dev server.
    private async Task<Endpoint> StartEndpointAsync(WebhookProvider provider, WebhookOptions? options = null)
    {
        var port = FreePorts.Pick(1)[0];
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenLocalhost(port));
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddInngest(inngest =>
        {
            inngest.AppId = "webhook-endpoint";
            inngest.IsDev = true;
            inngest.DevServerUrl = Api.BaseUrl;
        });

        var app = builder.Build();
        app.MapInngestWebhook("/webhook", provider, options);
        await app.StartAsync(Cancellation);
        return new Endpoint(app, new HttpClient { BaseAddress = new Uri($"http://localhost:{port}"), Timeout = TimeSpan.FromSeconds(30) });
    }

    private sealed class Endpoint(WebApplication app, HttpClient http) : IAsyncDisposable
    {
        public HttpClient Http => http;

        public async ValueTask DisposeAsync()
        {
            http.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
