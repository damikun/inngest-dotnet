using System.Text.Json;
using Inngest.IntegrationTests.Evaluation.Harness;
using Inngest.IntegrationTests.Harness;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.AI;

// A dev server and an app whose functions call models: through Microsoft.Extensions.AI with OpenTelemetry, for the AI
// metadata of their steps, and through inference steps, which the dev server sends to stand-ins for the providers that
// the app serves itself and that record what they get.
public sealed class ModelFixture : EvaluationFixture
{
    protected override void AddEvaluationFunctions(IInngestBuilder inngest)
    {
        // Where the dev server's container reaches the stand-ins.
        inngest.Services.AddSingleton(new FakeProviders(new Uri($"http://host.docker.internal:{AppPort}/providers/")));
        ModelFunctions.Add(inngest);
    }

    protected override void ConfigureApp(WebApplication app) =>
        app.MapPost("/providers/{testId}/{provider}/{**path}", async (HttpContext http, string testId, string provider) =>
        {
            using var body = await JsonDocument.ParseAsync(http.Request.Body);
            var request = new ProviderRequest(
                provider,
                http.Request.Path.Value![(http.Request.Path.Value!.IndexOf(provider, StringComparison.Ordinal) + provider.Length)..] + http.Request.QueryString,
                new Dictionary<string, string>(
                    http.Request.Headers
                        .Where(header => header.Key.ToLowerInvariant() is "authorization" or "x-api-key" or "api-key" or "anthropic-version" or "anthropic-beta")
                        .Select(header => KeyValuePair.Create(header.Key.ToLowerInvariant(), header.Value.ToString()))),
                body.RootElement.Clone());
            http.RequestServices.GetRequiredService<Probe>().For(testId).Record("provider", request);
            return Results.Text(FakeProviders.Answer(provider), "application/json");
        });
}

internal sealed record ProviderRequest(string Provider, string PathAndQuery, IReadOnlyDictionary<string, string> Headers, JsonElement Body);

internal sealed class FakeProviders(Uri baseUrl)
{
    public Uri For(string testId, string provider) => new(baseUrl, $"{testId}/{provider}/v1/");

    public static string Answer(string provider) => provider switch
    {
        "openai" or "grok" or "azure" =>
            $$$"""{"id":"chatcmpl-1","object":"chat.completion","created":1,"model":"gpt-4o-mini","choices":[{"index":0,"message":{"role":"assistant","content":"Hello from {{{provider}}}"},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":3,"total_tokens":8}}""",
        "responses" =>
            """{"id":"resp_1","object":"response","status":"completed","model":"gpt-5-mini","output":[{"type":"message","id":"msg_1","role":"assistant","status":"completed","content":[{"type":"output_text","text":"Hello from responses","annotations":[]}]}],"usage":{"input_tokens":5,"output_tokens":3,"total_tokens":8}}""",
        "anthropic" =>
            """{"id":"msg_1","type":"message","role":"assistant","model":"claude-sonnet-4-5","content":[{"type":"text","text":"Hello from anthropic"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":5,"output_tokens":3}}""",
        "gemini" =>
            """{"candidates":[{"content":{"role":"model","parts":[{"text":"Hello from gemini"}]},"finishReason":"STOP","index":0}],"usageMetadata":{"promptTokenCount":5,"candidatesTokenCount":3,"totalTokenCount":8},"modelVersion":"gemini-2.5-flash"}""",
        _ => "{}",
    };
}
