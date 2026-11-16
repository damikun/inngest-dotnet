using Inngest.IntegrationTests.Harness;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Inngest.IntegrationTests.DurableEndpoints;

// The dev server and an app with one durable endpoint, for a test that restarts the dev server while the app runs. It
// has a server of its own, since the restart takes every run and app away from the tests that share one.
public sealed class RestartedDevServerFixture : InngestFixture
{
    public override string AppId => "restarted-dev-server";

    public override IReadOnlyCollection<string> ExpectedFunctions => [];

    public Uri AppUrl => new($"http://localhost:{AppPort}");

    protected override string EventKey => "itests";

    protected override InngestServer CreateServer(int hostPort, Uri appUrl) => InngestServer.Dev(hostPort, appUrl);

    protected override void ConfigureInngest(InngestOptions options, Uri serverUrl)
    {
        options.AppId = AppId;
        options.IsDev = true;
        options.DevServerUrl = serverUrl;
        options.ServeOrigin = new Uri($"http://host.docker.internal:{AppPort}");
    }

    protected override void AddFunctions(IInngestBuilder inngest)
    {
    }

    protected override void ConfigureApp(WebApplication app)
    {
        app.UseInngestDurableEndpoints();
        app.MapPost("/ship", async (IStepTools step) =>
        {
            var packed = await step.RunAsync("pack", () => 1);
            await step.SleepAsync("cool-off", TimeSpan.FromSeconds(1));
            return Results.Ok(new { packed });
        }).WithDurableEndpoint("ship");
    }
}
