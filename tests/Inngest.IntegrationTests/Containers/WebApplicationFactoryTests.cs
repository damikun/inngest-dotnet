using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Inngest.IntegrationTests.Harness;
using Inngest.Sample.Functions;
using Inngest.Testing.Containers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Inngest.IntegrationTests.Containers;

// The sample app of the repository, under WebApplicationFactory on a real socket, against a server in a container. Its
// Program is internal, and any public type of the assembly finds the entry point just as well.
public abstract class FactoryFixture : IAsyncLifetime
{
    public InngestServerContainer Server { get; private set; } = null!;

    public InngestWebApplicationFactory<OnboardUser> App { get; private set; } = null!;

    public DevServerClient Api { get; private set; } = null!;

    protected abstract InngestServerBuilder Configure(InngestServerBuilder builder);

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Server = Configure(new InngestServerBuilder(InngestServer.Image)).Build();
        try
        {
            await Server.StartAsync(cancellationToken);
            App = new InngestWebApplicationFactory<OnboardUser>(Server);
            await App.StartAsync(cancellationToken);
            Api = new DevServerClient(Server.GetBaseAddress(), Server.EventKey ?? "test", Server.SigningKey, DescribeAsync);
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task<string> DescribeAsync(CancellationToken cancellationToken)
    {
        var (stdout, stderr) = await Server.GetLogsAsync(timestampsEnabled: false, ct: cancellationToken);
        return "--- Inngest server log tail ---\n" + string.Join('\n', (stdout + stderr).Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(30));
    }

    public async ValueTask DisposeAsync()
    {
        // The server goes first, so it stops calling an app that is shutting down.
        if (Server is not null)
        {
            await Server.DisposeAsync();
        }

        if (App is not null)
        {
            await App.DisposeAsync();
        }

        Api?.Dispose();
        GC.SuppressFinalize(this);
    }
}

public sealed class DevFactoryFixture : FactoryFixture
{
    protected override InngestServerBuilder Configure(InngestServerBuilder builder) => builder;
}

public sealed class SelfHostedFactoryFixture : FactoryFixture
{
    protected override InngestServerBuilder Configure(InngestServerBuilder builder) => builder.WithSelfHosting();
}

public abstract class WebApplicationFactoryTests<TFixture>(TFixture fixture)
    where TFixture : FactoryFixture
{
    protected TFixture Fixture { get; } = fixture;

    protected static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact(Timeout = 60_000)]
    public async Task The_server_runs_a_function_of_the_app_over_a_real_socket()
    {
        var eventId = await Fixture.Api.SendEventAsync("demo/hello", new { name = "Ada" }, Cancellation);

        var run = await Fixture.Api.WaitForRunEndAsync(eventId, Cancellation);

        run.Status.ShouldBe(RunStatus.Completed);
        run.Output.ShouldNotBeNull().ShouldContainJson("""{ "greeting": "Hello, Ada!" }""");
    }

    [Fact(Timeout = 60_000)]
    public async Task A_request_to_the_app_sends_an_event_through_the_apps_own_client_and_the_server_starts_the_run()
    {
        using var client = Fixture.App.CreateClient();

        using var response = await client.PostAsJsonAsync("/signup", new { email = "ada@example.com" }, Cancellation);
        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>(Cancellation);
        var run = await Fixture.Api.WaitForRunAsync(accepted.GetProperty("eventId").GetString()!, Cancellation);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        // The first step has run, so the server called the app for it, and the run now waits for the user to activate.
        await Fixture.Api.WaitForStepAsync(run.RunId, StepOps.WaitForEvent, SpanStatuses.Waiting, Cancellation);
    }

    [Fact(Timeout = 60_000)]
    public async Task The_endpoint_answers_clients_on_the_same_socket_the_server_calls()
    {
        using var client = Fixture.App.CreateClient();
        var url = Fixture.App.GetAppUrl();

        client.BaseAddress.ShouldNotBeNull().Port.ShouldBe(url.Port);
        using var response = await client.GetAsync(url.PathAndQuery, Cancellation);

        // The app in cloud mode answers only signed requests, even for introspection.
        response.StatusCode.ShouldBe(Fixture.Server.IsDevServer ? HttpStatusCode.OK : HttpStatusCode.Unauthorized);
    }

    [Fact(Timeout = 60_000)]
    public void The_app_is_set_up_for_the_container_whatever_it_set_for_itself()
    {
        var options = Fixture.App.Services.GetRequiredService<IOptions<InngestOptions>>().Value;

        options.IsDev.ShouldBe(Fixture.Server.IsDevServer);
        options.BaseUrl.ShouldBe(Fixture.Server.GetBaseAddress());
        options.SigningKey.ShouldBe(Fixture.Server.SigningKey);
        options.EventKey.ShouldBe(Fixture.Server.EventKey);
    }

    [Fact(Timeout = 60_000)]
    public void The_in_memory_server_is_not_there_because_the_app_runs_on_kestrel()
    {
        Should.Throw<NotSupportedException>(() => Fixture.App.Server);
    }

    [Fact(Timeout = 60_000)]
    public void Starting_the_app_again_is_harmless()
    {
        Should.NotThrow(() => Fixture.App.StartAsync(Cancellation).GetAwaiter().GetResult());
    }
}

// Every way to get a client, which does not depend on the server, is checked once here.
public sealed class DevWebApplicationFactoryTests(DevFactoryFixture fixture)
    : WebApplicationFactoryTests<DevFactoryFixture>(fixture), IClassFixture<DevFactoryFixture>
{
    // Kestrel reports the app as [::], and the factory hands http://localhost, port 80, to options nobody gave an address.
    private async Task ShouldReachTheApp(HttpClient client)
    {
        var url = Fixture.App.GetAppUrl();

        client.BaseAddress.ShouldNotBeNull().Host.ShouldBe("localhost");
        client.BaseAddress.Port.ShouldBe(url.Port);
        using var response = await client.GetAsync(url.PathAndQuery, Cancellation);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact(Timeout = 60_000)]
    public async Task CreateClient_reaches_the_app()
    {
        using var client = Fixture.App.CreateClient();

        await ShouldReachTheApp(client);
    }

    [Fact(Timeout = 60_000)]
    public async Task CreateClient_with_options_that_have_no_base_address_reaches_the_app()
    {
        var options = new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false, MaxAutomaticRedirections = 3 };

        using var client = Fixture.App.CreateClient(options);

        await ShouldReachTheApp(client);

        // The options are the caller's own, so they are left as they were.
        options.BaseAddress.ShouldBe(new Uri("http://localhost"));
    }

    [Fact(Timeout = 60_000)]
    public void CreateClient_keeps_a_base_address_it_was_given()
    {
        var elsewhere = new Uri("http://example.test:81/");

        using var client = Fixture.App.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = elsewhere });

        client.BaseAddress.ShouldBe(elsewhere);
    }

    [Fact(Timeout = 60_000)]
    public async Task CreateDefaultClient_reaches_the_app()
    {
        using var client = Fixture.App.CreateDefaultClient();

        await ShouldReachTheApp(client);
    }

    [Fact(Timeout = 60_000)]
    public async Task CreateDefaultClient_with_a_handler_reaches_the_app_through_it()
    {
        var handler = new CountingHandler();

        using var client = Fixture.App.CreateDefaultClient(handler);

        await ShouldReachTheApp(client);
        handler.Calls.ShouldBe(1);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_client_can_be_made_before_the_app_has_been_synced()
    {
        // The first thing a test does with a factory may be to create a client, which starts the app on its own.
        await using var factory = new InngestWebApplicationFactory<OnboardUser>(Fixture.Server);

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync(factory.GetAppUrl().PathAndQuery, Cancellation);

        client.BaseAddress.ShouldNotBeNull().Host.ShouldBe("localhost");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_factory_made_with_WithWebHostBuilder_keeps_the_inngest_settings_but_runs_in_memory()
    {
        using var derived = Fixture.App.WithWebHostBuilder(builder => builder.UseSetting("Logging:LogLevel:Default", "Warning"));

        var options = derived.Services.GetRequiredService<IOptions<InngestOptions>>().Value;
        using var client = derived.CreateClient();
        using var response = await client.GetAsync("/api/inngest", Cancellation);

        // Another instance of the app, on the factory's in-memory server, which the Kestrel one does not have.
        options.BaseUrl.ShouldBe(Fixture.Server.GetBaseAddress());
        options.IsDev.ShouldBe(true);
        derived.Services.ShouldNotBeSameAs(Fixture.App.Services);
        derived.Server.ShouldNotBeNull();
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private sealed class CountingHandler : DelegatingHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return base.SendAsync(request, cancellationToken);
        }
    }
}

public sealed class SelfHostedWebApplicationFactoryTests(SelfHostedFactoryFixture fixture)
    : WebApplicationFactoryTests<SelfHostedFactoryFixture>(fixture), IClassFixture<SelfHostedFactoryFixture>
{
    [Fact(Timeout = 60_000)]
    public async Task The_app_checks_the_signatures_of_the_server_that_hold_the_containers_signing_key()
    {
        // An unsigned call to the app is refused in cloud mode, the server's signed ones are not.
        using var client = Fixture.App.CreateClient();
        using var unsigned = await client.PostAsync(Fixture.App.GetAppUrl().PathAndQuery + "?fnId=inngest-dotnet-sample-hello-world&stepId=step", new StringContent("{}"), Cancellation);
        var eventId = await Fixture.Api.SendEventAsync("demo/hello", new { name = "Grace" }, Cancellation);
        var run = await Fixture.Api.WaitForRunEndAsync(eventId, Cancellation);

        unsigned.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        run.Status.ShouldBe(RunStatus.Completed);
    }
}

public sealed class WebApplicationFactoryStartTests
{
    [Fact(Timeout = 60_000)]
    public async Task The_app_does_not_start_before_the_container_runs()
    {
        await using var server = new InngestServerBuilder(InngestServer.Image).Build();
        await using var app = new InngestWebApplicationFactory<OnboardUser>(server);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => app.StartAsync(TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("must be running");
    }
}
