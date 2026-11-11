using Inngest.IntegrationTests.Harness;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Inngest.IntegrationTests.DurableEndpoints;

// The dev server and an app in dev mode with the encryption middleware, so a resumed durable endpoint's output reaches
// Inngest encrypted. The endpoint sends its client to the app's output proxy, which decrypts it.
public sealed class EncryptedDurableEndpointFixture : InngestFixture
{
    public const string Secret = "a-secret-the-dev-server-never-sees";

    public override string AppId => "encrypted-endpoints";

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

    protected override void AddFunctions(IInngestBuilder inngest) =>
        inngest.AddEncryption(options => options.Key = "0123456789abcdef0123456789abcdef");

    protected override void ConfigureApp(WebApplication app)
    {
        app.UseInngestDurableEndpoints();
        app.MapInngestDurableEndpointProxy("/inngest/output");

        app.MapPost("/secret-now/{testId}", async (string testId, IStepTools step) =>
        {
            var note = await step.RunAsync("load", () => Secret);
            return Results.Text($"{testId}: {note}", "text/plain");
        }).WithDurableEndpoint("secret-now");

        app.MapPost("/secret/{testId}", async (string testId, IStepTools step) =>
        {
            var note = await step.RunAsync("load", () => Secret);
            await step.SleepAsync("cool-off", TimeSpan.FromSeconds(1));
            return Results.Text($"{testId}: {note}", "text/plain", statusCode: StatusCodes.Status202Accepted);
        }).WithDurableEndpoint(new DurableEndpointOptions
        {
            Id = "secret",
            RedirectUrl = run => DurableEndpointFixture.ProxyUrl(AppUrl, run),
        });
    }
}
