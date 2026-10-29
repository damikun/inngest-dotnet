using System.Text.Json;
using Inngest.IntegrationTests.Evaluation.Harness;
using Inngest.IntegrationTests.Harness;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.Gateway;

// A dev server and an app whose functions wait for and send signals, and have Inngest make HTTP requests and AI calls
// for them. The app also serves the endpoints those requests go to, so nothing leaves the machine.
public sealed class GatewayFixture : EvaluationFixture
{
    // Where the Inngest server reaches the fake endpoints, and where the app itself does.
    public string ServerUrl(string path) => new Uri(App.ServerFacingUrl, path).AbsoluteUri;

    public string LocalUrl(string path) => new Uri(App.LocalUrl, path).AbsoluteUri;

    protected override void AddEvaluationFunctions(IInngestBuilder inngest)
    {
        inngest.Services.AddSingleton<IStartupFilter, FakeEndpoints>();
        GatewayFunctions.Add(inngest);
    }
}

// A request one of the fake endpoints received.
public sealed record ReceivedRequest(string Method, string PathAndQuery, string Body, IReadOnlyDictionary<string, string> Headers)
{
    public string? Header(string name) =>
        Headers.FirstOrDefault(header => string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}

// The fake endpoints run ahead of the Inngest endpoint and record every request under the test id it carries in a
// header: /fake/echo answers 201 with what it received, and /fake/openai/v1/chat/completions answers like OpenAI's chat
// completions API.
internal sealed class FakeEndpoints(Probe probe) : IStartupFilter
{
    public const string TestIdHeader = "X-Test-Id";
    public const string EchoPath = "/fake/echo";
    public const string ChatPath = "/fake/openai/v1/chat/completions";
    public const string Reply = "Hello from the fake model";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            var path = context.Request.Path.Value;
            if (path is not (EchoPath or ChatPath))
            {
                await nextMiddleware(context);
                return;
            }

            var received = await ReadAsync(context.Request);
            probe.For(received.Header(TestIdHeader) ?? "none").Record(path, received);
            if (path == EchoPath)
            {
                context.Response.StatusCode = StatusCodes.Status201Created;
                context.Response.Headers["X-Echo"] = "yes";
                await context.Response.WriteAsJsonAsync(received);
            }
            else
            {
                using var body = JsonDocument.Parse(received.Body);
                var model = body.RootElement.GetProperty("model").GetString();
                await context.Response.WriteAsJsonAsync(new
                {
                    id = "chatcmpl-fake",
                    @object = "chat.completion",
                    created = 1_700_000_000,
                    model,
                    choices = new[] { new { index = 0, message = new { role = "assistant", content = Reply }, finish_reason = "stop" } },
                    usage = new { prompt_tokens = 3, completion_tokens = 5, total_tokens = 8 },
                });
            }
        });
        next(app);
    };

    private static async Task<ReceivedRequest> ReadAsync(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync();
        var headers = request.Headers.ToDictionary(header => header.Key, header => header.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        return new ReceivedRequest(request.Method, $"{request.Path}{request.QueryString}", body, headers);
    }
}
