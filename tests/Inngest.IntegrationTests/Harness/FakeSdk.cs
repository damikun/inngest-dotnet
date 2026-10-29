using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Inngest.IntegrationTests.Harness;

// A minimal hand-written SDK that answers the dev server the way captured traffic shows. It lets the harness check its
// own helpers against real server responses without relying on the SDK under test. Each instance registers its own
// app with its own event names, so fake apps never trigger each other's functions.
//
// Functions: "echo" returns its event data, "fail" fails without retries, "wait" waits for a "resume" event with the
// same data.key, and "sleep" sleeps until a "cancel" event with the same data.key cancels it.
public sealed class FakeSdk : IAsyncDisposable
{
    private const string Path = "/api/fake";
    private const string SdkHeader = "fake:v0.0.1";

    private static readonly string ResumeStepId = HashStepId("resume");
    private static readonly string NapStepId = HashStepId("nap");

    private readonly WebApplication _app;
    private readonly DevServerClient _api;

    private FakeSdk(WebApplication app, DevServerClient api, string appId, Uri url)
    {
        _app = app;
        _api = api;
        AppId = appId;
        Url = url;
    }

    public string AppId { get; }

    public Uri Url { get; }

    public IReadOnlyList<string> Slugs => [Slug("echo"), Slug("fail"), Slug("wait"), Slug("sleep")];

    public string EventName(string name) => $"{AppId}/{name}";

    public string Slug(string function) => $"{AppId}-{function}";

    public static async Task<FakeSdk> StartAsync(DevServerClient api, CancellationToken cancellationToken)
    {
        var appId = $"harness-{Guid.NewGuid():N}"[..20];
        var port = FreePorts.Pick(1)[0];

        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenAnyIP(port));
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.MapPost(Path, context => HandleAsync(context, appId));
        await app.StartAsync(cancellationToken);

        var fake = new FakeSdk(app, api, appId, new Uri($"http://host.docker.internal:{port}{Path}"));
        try
        {
            await api.RegisterAsync(fake.Registration(), cancellationToken);
        }
        catch
        {
            await fake.DisposeAsync();
            throw;
        }

        return fake;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _api.RemoveAppAsync(Url, CancellationToken.None);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
        {
            // Removing the app only keeps the shared server tidy; a failure here must not fail the test.
        }

        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static string HashStepId(string id) => Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(id)));

    private string Registration()
    {
        var cancel = $$""", "cancel": [{ "event": "{{EventName("cancel")}}", "if": "event.data.key == async.data.key" }]""";
        return $$"""
            {
              "url": "{{Url}}", "deployType": "ping", "appName": "{{AppId}}", "sdk": "{{SdkHeader}}", "v": "0.1", "framework": "fake",
              "functions": [{{Function("echo")}}, {{Function("fail")}}, {{Function("wait")}}, {{Function("sleep", cancel)}}]
            }
            """;
    }

    private string Function(string name, string extra = "") => $$"""
        {
          "id": "{{Slug(name)}}", "name": "{{name}}", "triggers": [{ "event": "{{EventName(name)}}" }],
          "steps": { "step": { "id": "step", "name": "step", "runtime": { "type": "http", "url": "{{Url}}?fnId={{Slug(name)}}&stepId=step" } } }{{extra}}
        }
        """;

    private static async Task HandleAsync(HttpContext context, string appId)
    {
        using var call = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
        var steps = call.RootElement.GetProperty("steps");
        var data = call.RootElement.GetProperty("event").GetProperty("data");
        var function = context.Request.Query["fnId"].ToString()[(appId.Length + 1)..];

        // Without X-Inngest-SDK the server takes a 2xx for a response from something other than an SDK.
        context.Response.Headers["X-Inngest-SDK"] = SdkHeader;
        context.Response.Headers["X-Inngest-Req-Version"] = "2";
        context.Response.ContentType = "application/json";

        var (status, body) = function switch
        {
            "echo" => (200, $$"""{ "echo": {{data.GetRawText()}} }"""),
            "fail" => (400, """{ "name": "Error", "message": "The fake failed" }"""),
            "wait" when steps.TryGetProperty(ResumeStepId, out var resumed) => (200, $$"""{ "resumed": {{resumed.GetRawText()}} }"""),
            "wait" => (206, $$"""
                [{ "id": "{{ResumeStepId}}", "op": "WaitForEvent", "name": "{{appId}}/resume", "displayName": "resume",
                   "opts": { "event": "{{appId}}/resume", "timeout": "30s", "if": "event.data.key == async.data.key" } }]
                """),
            "sleep" when steps.TryGetProperty(NapStepId, out _) => (200, "{}"),
            "sleep" => (206, $$"""
                [{ "id": "{{NapStepId}}", "op": "Sleep", "name": "30s", "displayName": "nap", "opts": { "duration": "30s" } }]
                """),
            _ => (404, $$"""{ "message": "No fake function {{function}}" }"""),
        };

        if (status == 400)
        {
            context.Response.Headers["X-Inngest-No-Retry"] = "true";
        }

        context.Response.StatusCode = status;
        await context.Response.WriteAsync(body, context.RequestAborted);
    }
}
