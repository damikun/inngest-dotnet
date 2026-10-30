using Aspire.Hosting.Testing;
using System.Net;
using System.Net.Http.Headers;

namespace Inngest.Aspire.Hosting.Tests.Containers;

// The self-hosted server, which signs every request and wants its keys on every call, next to a project that has them.
public sealed class SelfHostedTests(SelfHostedApp fixture) : IClassFixture<SelfHostedApp>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    private async Task<(ServerApi Api, string EventKey)> ConnectAsync(CancellationToken cancellationToken)
    {
        await fixture.WaitForHealthyAsync("inngest", cancellationToken);
        await fixture.WaitForHealthyAsync("shop", cancellationToken);

        // The server accepts the plain signing key as the bearer token of its REST API.
        var signingKey = await fixture.Server.SigningKeyParameter!.GetValueAsync(cancellationToken);
        var eventKey = await fixture.Server.EventKeyParameter!.GetValueAsync(cancellationToken);
        var http = fixture.App.CreateHttpClient("inngest", "http");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", signingKey);
        return (new ServerApi(http, eventKey!), eventKey!);
    }

    [Fact]
    public async Task The_server_is_healthy_and_the_dashboard_links_its_UI()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.WaitForHealthyAsync("inngest", cancellationToken);

        using var http = fixture.App.CreateHttpClient("inngest", "http");
        using var health = await http.GetAsync("health", cancellationToken);
        health.StatusCode.ShouldBe(HttpStatusCode.OK);

        fixture.App.ResourceNotifications.TryGetCurrentState("inngest", out var resource).ShouldBeTrue();
        resource!.Snapshot.Urls.ShouldContain(url => url.DisplayProperties.DisplayName == "Inngest UI" && !url.IsInternal);
    }

    [Fact]
    public async Task The_server_and_the_project_share_the_keys_and_the_signed_calls_run_the_function()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (server, _) = await ConnectAsync(cancellationToken);

        // A sync with a signature the project cannot verify, or a call the project cannot verify, would not get this far.
        await server.WaitForFunctionAsync("aspire-test-app", "aspire-test-app-hello", Timeout, cancellationToken);
        using var shop = fixture.App.CreateHttpClient("shop", "http");
        using var sent = await shop.PostAsync("send", content: null, cancellationToken);
        sent.EnsureSuccessStatusCode();
        using var sentBody = System.Text.Json.JsonDocument.Parse(await sent.Content.ReadAsStringAsync(cancellationToken));
        var run = await server.WaitForRunAsync(sentBody.RootElement.GetProperty("id").GetString()!, Timeout, cancellationToken);

        run.Status.ShouldBe("Completed");
        run.Output.GetProperty("greeting").GetString().ShouldBe("hello from aspire");
    }

    [Fact]
    public async Task The_project_is_in_cloud_mode_and_refuses_what_is_not_signed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.WaitForHealthyAsync("shop", cancellationToken);

        using var shop = fixture.App.CreateHttpClient("shop", "http");
        using var unsigned = await shop.GetAsync("api/inngest", cancellationToken);

        unsigned.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_server_refuses_an_event_with_another_key()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (server, _) = await ConnectAsync(cancellationToken);

        using var refused = await server.SendEventAsync("not-the-event-key", "aspire/hello", cancellationToken);

        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
