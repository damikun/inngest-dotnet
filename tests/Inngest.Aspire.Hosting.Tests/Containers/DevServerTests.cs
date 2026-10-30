using Aspire.Hosting.Testing;
using System.Net;

namespace Inngest.Aspire.Hosting.Tests.Containers;

// The dev server in Docker next to a project on the host, started by Aspire. They prove what the app model cannot: that
// the addresses Aspire works out are the ones that work, in both directions.
public sealed class DevServerTests(DevServerApp fixture) : IClassFixture<DevServerApp>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    [Fact]
    public async Task The_server_becomes_healthy_and_the_dashboard_links_its_UI()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await fixture.WaitForHealthyAsync("inngest", cancellationToken);

        using var http = fixture.App.CreateHttpClient("inngest", "http");
        using var health = await http.GetAsync("health", cancellationToken);
        health.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var ui = await http.GetAsync("", cancellationToken);
        ui.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ui.Content.ReadAsStringAsync(cancellationToken)).ShouldContain("<html", Case.Insensitive);

        fixture.App.ResourceNotifications.TryGetCurrentState("inngest", out var resource).ShouldBeTrue();
        var urls = resource!.Snapshot.Urls;
        urls.ShouldContain(url => url.DisplayProperties.DisplayName == "Inngest UI" && !url.IsInternal);
        urls.ShouldContain(url => url.IsInternal);
    }

    [Fact]
    public async Task The_gateway_answers_on_the_port_Aspire_publishes_it_on()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.WaitForHealthyAsync("inngest", cancellationToken);

        using var http = new HttpClient();
        using var ready = await http.GetAsync(new UriBuilder(fixture.App.GetEndpoint("inngest", "connect")) { Path = "ready" }.Uri, cancellationToken);

        ready.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // The server would tell the worker to dial the port the gateway has in the container. The address the worker is given
    // instead is that of the endpoint, on the port Aspire published.
    [Fact]
    public async Task A_Connect_worker_dials_the_gateway_where_Aspire_published_it_and_runs_its_function()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.WaitForHealthyAsync("inngest", cancellationToken);
        await fixture.WaitForHealthyAsync("worker", cancellationToken);
        var server = new ServerApi(fixture.App.CreateHttpClient("inngest", "http"), "dev-key");

        // The app appears when the worker has connected, and the server cannot call it over HTTP.
        await server.WaitForFunctionAsync("aspire-test-worker", "aspire-test-worker-hello", Timeout, cancellationToken);
        using var worker = fixture.App.CreateHttpClient("worker", "http");
        using var sent = await worker.PostAsync("send", content: null, cancellationToken);
        sent.EnsureSuccessStatusCode();
        using var sentBody = System.Text.Json.JsonDocument.Parse(await sent.Content.ReadAsStringAsync(cancellationToken));
        var run = await server.WaitForRunAsync(sentBody.RootElement.GetProperty("id").GetString()!, Timeout, cancellationToken);

        run.Status.ShouldBe("Completed");
        run.Output.GetProperty("greeting").GetString().ShouldBe("hello from aspire");
    }

    [Fact]
    public async Task The_server_syncs_the_project_through_the_address_Aspire_gives_the_container_and_runs_its_function()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.WaitForHealthyAsync("inngest", cancellationToken);
        await fixture.WaitForHealthyAsync("shop", cancellationToken);
        var server = new ServerApi(fixture.App.CreateHttpClient("inngest", "http"), "dev-key");

        // The sync reaches the app from the container, and each step of the run calls it again.
        var function = await server.WaitForFunctionAsync("aspire-test-app", "aspire-test-app-hello", Timeout, cancellationToken);
        var eventId = await server.SendEventAsync("aspire/hello", cancellationToken);
        var run = await server.WaitForRunAsync(eventId, Timeout, cancellationToken);

        run.Status.ShouldBe("Completed");
        run.Output.GetProperty("greeting").GetString().ShouldBe("hello from aspire");

        // The server calls the project at the address that Aspire gave the container for the host, not at localhost, which
        // would be the container itself.
        var stepUrl = new Uri(function.StepUrl);
        stepUrl.IsLoopback.ShouldBeFalse();
        stepUrl.AbsolutePath.ShouldBe("/api/inngest");
    }

    [Fact]
    public async Task The_project_finds_the_server_from_its_settings_and_the_app_is_in_dev_mode()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.WaitForHealthyAsync("inngest", cancellationToken);
        await fixture.WaitForHealthyAsync("shop", cancellationToken);
        var server = new ServerApi(fixture.App.CreateHttpClient("inngest", "http"), "dev-key");

        // The app sends the event to INNGEST_BASE_URL, and the server runs the function.
        using var shop = fixture.App.CreateHttpClient("shop", "http");
        using var sent = await shop.PostAsync("send", content: null, cancellationToken);
        sent.EnsureSuccessStatusCode();
        using var sentBody = System.Text.Json.JsonDocument.Parse(await sent.Content.ReadAsStringAsync(cancellationToken));
        var run = await server.WaitForRunAsync(sentBody.RootElement.GetProperty("id").GetString()!, Timeout, cancellationToken);
        run.Status.ShouldBe("Completed");

        // Dev mode answers an introspection request without a signature.
        using var introspection = await shop.GetAsync("api/inngest", cancellationToken);
        introspection.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var introspectionBody = System.Text.Json.JsonDocument.Parse(await introspection.Content.ReadAsStringAsync(cancellationToken));
        introspectionBody.RootElement.GetProperty("mode").GetString().ShouldBe("dev");
    }
}
