using System.Net.Http.Json;
using System.Text.Json;

namespace Inngest.Aspire.Hosting.Tests.Containers;

internal sealed record SyncedFunction(string Slug, string StepUrl);

internal sealed record RunResult(string Status, JsonElement Output);

// The Inngest server's own API, which is how the tests see what the server did with an app: the functions it synced and
// the run an event started. The REST reads are cached per URL, so every GET carries a query parameter of its own.
internal sealed class ServerApi(HttpClient http, string eventKey)
{
    private long _requests;

    // The function once the server has synced the app, with the URL it calls for each step: that of the app, or for a
    // Connect worker not one a client could open.
    public async Task<SyncedFunction> WaitForFunctionAsync(string appId, string slug, TimeSpan timeout, CancellationToken cancellationToken) =>
        await Poll.UntilAsync(
            $"app {appId} to be synced with {slug}",
            async token =>
            {
                using var body = await GetAsync($"v1/apps/{appId}/functions", token);
                var function = body.RootElement.EnumerateArray().FirstOrDefault(candidate => candidate.GetProperty("id").GetString() == slug);
                return function.ValueKind == JsonValueKind.Object
                    ? new SyncedFunction(slug, function.GetProperty("config").GetProperty("steps")[0].GetProperty("uri").GetString()!)
                    : null;
            },
            timeout,
            cancellationToken);

    public async Task<HttpResponseMessage> SendEventAsync(string key, string name, CancellationToken cancellationToken) =>
        await http.PostAsJsonAsync($"e/{Uri.EscapeDataString(key)}", new[] { new { name, data = new { } } }, cancellationToken);

    public async Task<string> SendEventAsync(string name, CancellationToken cancellationToken)
    {
        using var response = await SendEventAsync(eventKey, name, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return body.RootElement.GetProperty("ids").EnumerateArray().Single().GetString()!;
    }

    // The run the event started, once it has ended.
    public async Task<RunResult> WaitForRunAsync(string eventId, TimeSpan timeout, CancellationToken cancellationToken) =>
        await Poll.UntilAsync(
            $"the run of event {eventId} to end",
            async token =>
            {
                using var body = await GetAsync($"v1/events/{eventId}/runs", token);
                if (!body.RootElement.TryGetProperty("data", out var runs) || runs.ValueKind != JsonValueKind.Array || runs.GetArrayLength() == 0)
                {
                    return null;
                }

                var run = runs[0];
                return run.TryGetProperty("ended_at", out var ended) && ended.ValueKind == JsonValueKind.String
                    ? new RunResult(run.GetProperty("status").GetString()!, run.TryGetProperty("output", out var output) ? output.Clone() : default)
                    : null;
            },
            timeout,
            cancellationToken);

    private async Task<JsonDocument> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"{path}?cb={Interlocked.Increment(ref _requests)}", cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return response.IsSuccessStatusCode
            ? JsonDocument.Parse(text)
            : throw new InvalidOperationException($"GET {path} returned {(int)response.StatusCode}: {text}");
    }
}
