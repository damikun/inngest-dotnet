using System.Net;
using Inngest.IntegrationTests.Harness;
using Inngest.Testing.Containers;

namespace Inngest.IntegrationTests.Containers;

// The settings as variables, for a process that inherits its parent's. Configuration stands in for the environment of
// that process here, with the variables of a developer's machine below the ones the container lists. SdkSettingsTests
// checks the precedence against the SDK's own settings; this is the one end-to-end check of that path for each server.
public sealed class EnvironmentContainerTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory(Timeout = 90_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_app_configured_by_the_variables_runs_its_functions_whatever_the_machine_has_set(bool selfHosted)
    {
        var builder = new InngestServerBuilder(InngestServer.Image);
        await using var server = (selfHosted ? builder.WithSelfHosting() : builder).Build();
        await server.StartAsync(Cancellation);

        // Dev mode, and the addresses and keys of Inngest Cloud: what would win without the container's variables.
        var variables = new Dictionary<string, string>
        {
            ["INNGEST_DEV"] = "1",
            ["INNGEST_BASE_URL"] = "https://api.inngest.com",
            ["INNGEST_API_BASE_URL"] = "https://api.inngest.com",
            ["INNGEST_EVENT_API_BASE_URL"] = "https://inn.gs",
            ["INNGEST_EVENT_KEY"] = "from-the-machine",
            ["INNGEST_SIGNING_KEY"] = "signkey-prod-0123456789abcdef",
        };
        foreach (var (name, value) in server.GetSdkEnvironment())
        {
            variables[name] = value;
        }

        await using var app = await ContainerApp.StartAsync(configure: null, variables, Cancellation);
        await server.SyncAsync(app.Url, Cancellation);
        using var api = new DevServerClient(server.GetBaseAddress(), server.EventKey ?? "test", server.SigningKey, _ => Task.FromResult(app.Describe()));

        var eventId = await api.SendEventAsync("containers/hello", new { name = "Ada" }, Cancellation);
        var run = await api.WaitForRunEndAsync(eventId, Cancellation);

        run.Status.ShouldBe(RunStatus.Completed);
        run.Output.ShouldNotBeNull().ShouldContainJson("""{ "greeting": "Hello, Ada!" }""");

        // A run completes in dev mode as well, so the mode is checked on its own: only an app in cloud mode refuses an
        // unsigned request.
        using var http = new HttpClient();
        using var unsigned = await http.GetAsync(app.Url, Cancellation);
        unsigned.StatusCode.ShouldBe(selfHosted ? HttpStatusCode.Unauthorized : HttpStatusCode.OK);
    }
}
