using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using Inngest.Tests.Execution;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inngest.Tests.Webhooks;

// MapInngestWebhook on an in-memory server, with Stripe as the provider and the client of the app recording what is sent.
public sealed class MapInngestWebhookTests
{
    private const string Secret = "whsec_Hk3pX9wZq2LmN7vR4tYb8cDf6gJa1sE5";
    private const string Path = "/webhooks/stripe";
    private const string Body = """{"id":"evt_1NG8Du2eZvKYlo2CUI8iWOzQ","type":"payment_intent.succeeded","data":{"object":{"id":"pi_1","amount":2000,"note":"private-note-7731"}}}""";

    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private readonly TestTimeProvider _time = new(Now);
    private readonly RecordingInngestClient _client = new();

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_signed_webhook_is_answered_with_200_and_no_body_and_becomes_an_event()
    {
        await using var app = await StartAsync();

        using var response = await Post(app, Body);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Cancellation)).ShouldBeEmpty();
        var sent = _client.Sent.ShouldHaveSingleItem();
        sent.Name.ShouldBe("stripe/payment_intent.succeeded");
        sent.Id.ShouldBe("evt_1NG8Du2eZvKYlo2CUI8iWOzQ");
        sent.DataJson.ShouldBe(Body);
    }

    [Fact]
    public async Task A_webhook_the_provider_delivers_again_is_sent_again_with_the_same_event_id()
    {
        await using var app = await StartAsync();

        using var first = await Post(app, Body);
        _time.Advance(TimeSpan.FromMinutes(3));
        using var second = await Post(app, Body);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        _client.Sent.Select(sent => sent.Id).ShouldBe(["evt_1NG8Du2eZvKYlo2CUI8iWOzQ", "evt_1NG8Du2eZvKYlo2CUI8iWOzQ"]);
    }

    [Fact]
    public async Task A_signature_of_another_secret_is_answered_with_401_and_nothing_is_said_or_sent()
    {
        await using var app = await StartAsync();

        using var response = await Post(app, Body, secret: "whsec_SomebodyElsesSecret0000000000000000");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync(Cancellation)).ShouldBeEmpty();
        response.Headers.Contains("WWW-Authenticate").ShouldBeFalse();
        _client.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_request_without_a_signature_is_answered_with_401()
    {
        await using var app = await StartAsync();

        using var response = await app.GetTestClient().PostAsync(Path, new StringContent(Body, Encoding.UTF8, "application/json"), Cancellation);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        _client.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_replay_after_the_tolerance_is_answered_with_401_even_though_its_signature_is_valid()
    {
        await using var app = await StartAsync();
        var timestamp = Now.AddMinutes(-6).ToUnixTimeSeconds();

        using var response = await Post(app, Body, timestamp: timestamp);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        _client.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_payload_that_is_not_json_is_answered_with_400_when_the_signature_is_valid_and_401_when_it_is_not()
    {
        await using var app = await StartAsync();

        using var valid = await Post(app, "payload=%7B%22id%22%3A1%7D");
        using var forged = await Post(app, "payload=%7B%22id%22%3A1%7D", secret: "whsec_SomebodyElsesSecret0000000000000000");

        valid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        forged.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        _client.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_body_that_says_it_is_over_the_limit_is_answered_with_413()
    {
        await using var app = await StartAsync(new WebhookOptions { MaxBodySize = 1024 });

        using var response = await Post(app, new string('x', 1025));

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        _client.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_body_without_a_length_is_cut_off_at_the_limit_and_answered_with_413()
    {
        await using var app = await StartAsync(new WebhookOptions { MaxBodySize = 1024 });
        using var content = new StreamContent(new UnseekableStream(new byte[100_000]));

        using var response = await app.GetTestClient().PostAsync(Path, content, Cancellation);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        _client.Sent.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_body_of_exactly_the_limit_is_accepted_whether_or_not_it_has_a_length(bool declareLength)
    {
        const int Limit = 70_000;
        var padding = Limit - Encoding.UTF8.GetByteCount("""{"id":"evt_1","type":"big","pad":""}""");
        var body = $$"""{"id":"evt_1","type":"big","pad":"{{new string('p', padding)}}"}""";
        Encoding.UTF8.GetByteCount(body).ShouldBe(Limit);
        await using var app = await StartAsync(new WebhookOptions { MaxBodySize = Limit });
        var bytes = Encoding.UTF8.GetBytes(body);
        using var content = declareLength ? (HttpContent)new ByteArrayContent(bytes) : new StreamContent(new UnseekableStream(bytes));
        using var request = new HttpRequestMessage(HttpMethod.Post, Path) { Content = content };
        request.Headers.Add("Stripe-Signature", Signing.StripeHeader(Secret, Now.ToUnixTimeSeconds(), bytes));

        using var response = await app.GetTestClient().SendAsync(request, Cancellation);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _client.Sent.ShouldHaveSingleItem().DataJson.ShouldBe(body);
    }

    [Fact]
    public async Task An_empty_body_is_answered_with_400_when_signed_and_401_when_not()
    {
        await using var app = await StartAsync();

        using var signed = await Post(app, "");
        using var unsigned = await app.GetTestClient().PostAsync(Path, new ByteArrayContent([]), Cancellation);

        signed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        unsigned.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_send_that_fails_is_answered_with_503_so_that_the_provider_delivers_the_webhook_again()
    {
        _client.Fail = new InngestException("Could not reach Inngest to send events: connection refused");
        await using var app = await StartAsync();

        using var response = await Post(app, Body);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync(Cancellation)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_send_that_never_finishes_is_answered_with_503_when_the_timeout_passes()
    {
        _client.Block = true;
        await using var app = await StartAsync(new WebhookOptions { SendTimeout = TimeSpan.FromSeconds(3) });

        var pending = Post(app, Body);
        await _time.TimersCreated(1);
        _time.Advance(TimeSpan.FromSeconds(3));
        using var response = await pending;

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task A_transform_can_drop_a_delivery_and_it_is_answered_with_204()
    {
        await using var app = await StartAsync(new WebhookOptions { Transform = (_, @event) => @event.Name.EndsWith(".succeeded", StringComparison.Ordinal) ? null : @event });

        using var response = await Post(app, Body);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        _client.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_transform_can_rename_the_event_and_choose_its_id()
    {
        await using var app = await StartAsync(new WebhookOptions
        {
            Transform = (delivery, @event) => @event with { Name = "billing/paid", Id = "stripe-" + @event.Id },
        });

        using var response = await Post(app, Body);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var sent = _client.Sent.ShouldHaveSingleItem();
        sent.Name.ShouldBe("billing/paid");
        sent.Id.ShouldBe("stripe-evt_1NG8Du2eZvKYlo2CUI8iWOzQ");
    }

    [Fact]
    public async Task Only_POST_is_routed()
    {
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        using var get = await client.GetAsync(Path, Cancellation);
        using var put = await client.PutAsync(Path, new StringContent(Body), Cancellation);

        get.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        put.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task The_endpoint_is_on_the_path_it_was_mapped_to()
    {
        await using var app = await StartAsync();

        using var response = await app.GetTestClient().PostAsync("/webhooks/other", new StringContent(Body), Cancellation);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_endpoint_gets_past_an_authorization_policy_that_covers_every_endpoint()
    {
        await using var app = await StartAsync(
            configure: app => app.UseAuthorization(),
            services: services =>
            {
                services.AddAuthentication(Nobody.Name).AddScheme<AuthenticationSchemeOptions, Nobody>(Nobody.Name, null);
                services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
            });

        using var response = await Post(app, Body);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Several_endpoints_for_several_providers_do_not_share_anything()
    {
        await using var app = await StartAsync(configure: null, services: null, extra: app =>
            app.MapInngestWebhook("/webhooks/github", WebhookProvider.GitHub("github secret")));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/github")
        {
            Content = new StringContent("""{"zen":"Keep it logically awesome."}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-GitHub-Event", "ping");
        request.Headers.Add("X-GitHub-Delivery", "72d3162e-cc78-11e3-81ab-4c9367dc0958");
        request.Headers.Add("X-Hub-Signature-256", "sha256=" + Signing.Hex(System.Security.Cryptography.HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("github secret"),
            Encoding.UTF8.GetBytes("""{"zen":"Keep it logically awesome."}"""))));

        using var github = await app.GetTestClient().SendAsync(request, Cancellation);
        using var stripeWithGitHubsSignature = await Post(app, Body, secret: "github secret");

        github.StatusCode.ShouldBe(HttpStatusCode.OK);
        stripeWithGitHubsSignature.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var sent = _client.Sent.ShouldHaveSingleItem();
        sent.Name.ShouldBe("github/ping");
        sent.Id.ShouldBe("72d3162e-cc78-11e3-81ab-4c9367dc0958");
    }

    [Fact]
    public async Task Many_deliveries_at_once_each_become_their_own_event()
    {
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 100).Select(async index =>
        {
            var body = Body.Replace("evt_1NG8Du2eZvKYlo2CUI8iWOzQ", $"evt_{index}", StringComparison.Ordinal);
            using var request = Signed(body, Secret, Now.ToUnixTimeSeconds());
            using var response = await client.SendAsync(request, Cancellation);
            return response.StatusCode;
        }));

        responses.ShouldAllBe(status => status == HttpStatusCode.OK);
        _client.Sent.Select(sent => sent.Id).Distinct().Count().ShouldBe(100);
    }

    [Fact]
    public async Task What_the_endpoint_logs_never_holds_a_secret_a_signature_or_a_body()
    {
        _client.Fail = new InngestException("Inngest API error: 500");
        await using var app = await StartAsync();

        using var failed = await Post(app, Body);
        using var forged = await Post(app, Body, secret: "whsec_SomebodyElsesSecret0000000000000000");
        using var broken = await Post(app, "private-note-7731 but not json");
        using var large = await app.GetTestClient().PostAsync(Path, new ByteArrayContent(new byte[2_000_000]), Cancellation);

        var logged = string.Join('\n', app.Services.GetFakeLogCollector().GetSnapshot().Select(record => record.Message + " " + record.Exception));
        logged.ShouldContain("Rejected a stripe webhook");
        logged.ShouldNotContain("Hk3pX9wZq2LmN7vR4tYb8cDf6gJa1sE5");
        logged.ShouldNotContain("SomebodyElsesSecret");
        logged.ShouldNotContain("private-note-7731");
        logged.ShouldNotContain("evt_1NG8Du2eZvKYlo2CUI8iWOzQ");
        logged.ShouldNotContain(Signing.Stripe(Secret, Now.ToUnixTimeSeconds(), Encoding.UTF8.GetBytes(Body)));
    }

    [Fact]
    public async Task MapInngestWebhook_requires_AddInngest()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();

        Should.Throw<InvalidOperationException>(() => app.MapInngestWebhook(Path, WebhookProvider.Stripe(Secret)))
            .Message.ShouldBe("Call services.AddInngest() before app.MapInngestWebhook().");
    }

    [Fact]
    public async Task MapInngestWebhook_checks_its_arguments_and_options_when_the_app_starts()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IInngestClient>(_client);
        await using var app = builder.Build();

        Should.Throw<ArgumentException>(() => app.MapInngestWebhook("", WebhookProvider.Stripe(Secret)));
        Should.Throw<ArgumentNullException>(() => app.MapInngestWebhook(Path, null!));
        Should.Throw<ArgumentOutOfRangeException>(() => app.MapInngestWebhook(Path, WebhookProvider.Stripe(Secret), new WebhookOptions { MaxBodySize = -1 }));
    }

    private static HttpRequestMessage Signed(string body, string secret, long timestamp)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("Stripe-Signature", Signing.StripeHeader(secret, timestamp, Encoding.UTF8.GetBytes(body)));
        return request;
    }

    private async Task<HttpResponseMessage> Post(WebApplication app, string body, string? secret = null, long? timestamp = null)
    {
        using var request = Signed(body, secret ?? Secret, timestamp ?? _time.GetUtcNow().ToUnixTimeSeconds());
        return await app.GetTestClient().SendAsync(request, Cancellation);
    }

    private async Task<WebApplication> StartAsync(
        WebhookOptions? options = null,
        Action<WebApplication>? configure = null,
        Action<IServiceCollection>? services = null,
        Action<WebApplication>? extra = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddFakeLogging();
        services?.Invoke(builder.Services);
        builder.Services.AddSingleton<IInngestClient>(_client);
        builder.Services.AddSingleton<TimeProvider>(_time);

        var app = builder.Build();
        configure?.Invoke(app);
        app.MapInngestWebhook(Path, WebhookProvider.Stripe(Secret), options);
        extra?.Invoke(app);
        await app.StartAsync(Cancellation);
        return app;
    }

    // Has no length, so the client sends the body chunked, as a proxy might.
    private sealed class UnseekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }

    // Authenticates nobody, so a policy that requires a user answers 401.
    private sealed class Nobody(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Name = "Nobody";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }
}
