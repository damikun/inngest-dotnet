using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Inngest.IntegrationTests.Harness;

// What tests need from the Inngest server's own API, independent of the SDK under test: sending events, following the
// runs they start and reading back the apps and functions the server knows.
//
// The REST GETs are cached on the server per URL (an event's runs for 15 s, even when there are none yet), so every
// GET carries a unique query parameter. Waits fail with the server's log and sync state attached.
public sealed class DevServerClient : IDisposable
{
    // Under the 30 s test timeout, so that a wait which runs out reports what it was waiting for.
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private const string RunTraceQuery = """
        query ($runID: String!) {
          run(runID: $runID) {
            status
            trace {
              name status stepOp
              childrenSpans { name status stepOp stepID childrenSpans { name status stepOp stepID } }
            }
          }
        }
        """;

    private const string AppsQuery = """
        { apps { externalID url error functionCount sdkLanguage sdkVersion framework } }
        """;

    private readonly HttpClient _http;
    private readonly string _eventKey;
    private readonly Func<CancellationToken, Task<string>> _describe;
    private long _requestCounter;

    public DevServerClient(Uri baseUrl, string eventKey, string? apiKey, Func<CancellationToken, Task<string>> describe)
    {
        _http = new HttpClient { BaseAddress = baseUrl, Timeout = TimeSpan.FromSeconds(15) };
        if (apiKey is not null)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        _eventKey = eventKey;
        _describe = describe;
    }

    public Uri BaseUrl => _http.BaseAddress!;

    // Returns the id the server gave the event, which is what its runs are looked up by.
    public async Task<string> SendEventAsync(string name, object data, CancellationToken cancellationToken)
    {
        var ids = await SendEventsAsync([new EventPayload(name, data)], cancellationToken);
        return ids.Single();
    }

    public async Task<IReadOnlyList<string>> SendEventsAsync(IReadOnlyList<EventPayload> events, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync($"e/{Uri.EscapeDataString(_eventKey)}", events, JsonSerializerOptions.Web, cancellationToken);
        using var body = await ReadJsonAsync(response, cancellationToken);
        return [.. body.RootElement.GetProperty("ids").EnumerateArray().Select(id => id.GetString() ?? string.Empty)];
    }

    public async Task<IReadOnlyList<RunInfo>> GetEventRunsAsync(string eventId, CancellationToken cancellationToken)
    {
        using var body = await GetJsonAsync($"v1/events/{eventId}/runs", cancellationToken);
        return body.RootElement.TryGetProperty("data", out var runs) && runs.ValueKind == JsonValueKind.Array
            ? [.. runs.EnumerateArray().Select(RunInfo.FromJson)]
            : [];
    }

    public async Task<RunInfo> GetRunAsync(string runId, CancellationToken cancellationToken)
    {
        using var body = await GetJsonAsync($"v1/runs/{runId}", cancellationToken);
        return RunInfo.FromJson(body.RootElement.GetProperty("data"));
    }

    // Waits for the event to start exactly one run; more than one means the test's event matched several functions.
    public async Task<RunInfo> WaitForRunAsync(string eventId, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var run = await Poll.UntilAsync(
            $"event {eventId} to start a run",
            async token => (await GetEventRunsAsync(eventId, token)) switch
            {
                [] => null,
                [var single] => single,
                var runs => throw new InvalidOperationException(
                    $"Event {eventId} started {runs.Count} runs, expected one: {string.Join(", ", runs)}"),
            },
            run => run is not null,
            timeout ?? DefaultTimeout,
            _describe,
            cancellationToken);

        return run!;
    }

    // Waits until the run the event started has ended, whether it completed, failed or was cancelled. The timeout
    // covers both finding the run and waiting for its end.
    public async Task<RunInfo> WaitForRunEndAsync(string eventId, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var budget = timeout ?? DefaultTimeout;
        var elapsed = Stopwatch.StartNew();
        var started = await WaitForRunAsync(eventId, cancellationToken, budget);

        // What finding the run left of the budget, but at least a second.
        var remaining = TimeSpan.FromTicks(Math.Max((budget - elapsed.Elapsed).Ticks, TimeSpan.TicksPerSecond));
        return await Poll.UntilAsync(
            $"run {started.RunId} (event {eventId}) to end",
            token => GetRunAsync(started.RunId, token),
            run => run.HasEnded,
            remaining,
            _describe,
            cancellationToken);
    }

    // Null until GraphQL can read the run, which is a moment after the REST API already lists it.
    public async Task<RunTrace?> GetRunTraceAsync(string runId, CancellationToken cancellationToken)
    {
        try
        {
            using var body = await QueryAsync(RunTraceQuery, new { runID = runId }, cancellationToken);
            var run = body.RootElement.GetProperty("data").GetProperty("run");
            return run.ValueKind == JsonValueKind.Object ? RunTrace.FromJson(run) : null;
        }
        catch (GraphQLException exception) when (exception.Errors.Contains("no rows in result set", StringComparison.Ordinal))
        {
            return null;
        }
    }

    public async Task<RunTrace> WaitForTraceAsync(
        string runId,
        string what,
        Func<RunTrace, bool> done,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var trace = await Poll.UntilAsync(
            $"the trace of run {runId} to show {what}",
            token => GetRunTraceAsync(runId, token),
            trace => trace is not null && done(trace),
            timeout ?? DefaultTimeout,
            _describe,
            cancellationToken);

        return trace!;
    }

    // Waits for a step of the given op (RUN, SLEEP, WAIT_FOR_EVENT, ...) to reach a status, for example a
    // wait-for-event step to be WAITING before the test sends the event that resumes it.
    public Task<RunTrace> WaitForStepAsync(
        string runId,
        string stepOp,
        string status,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null) =>
        WaitForTraceAsync(
            runId,
            $"a {stepOp} step that is {status}",
            trace => trace.Spans.Any(span => span.StepOp == stepOp && span.Status == status),
            cancellationToken,
            timeout);

    public async Task<IReadOnlyList<RegisteredFunction>> GetAppFunctionsAsync(string appId, CancellationToken cancellationToken)
    {
        using var body = await GetJsonAsync($"v1/apps/{Uri.EscapeDataString(appId)}/functions", cancellationToken);
        return [.. body.RootElement.EnumerateArray().Select(RegisteredFunction.FromJson)];
    }

    public Task<IReadOnlyList<RegisteredFunction>> WaitForFunctionsAsync(
        string appId,
        IReadOnlyCollection<string> slugs,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null) =>
        Poll.UntilAsync(
            $"app {appId} to be synced with its {slugs.Count} functions",
            token => GetAppFunctionsAsync(appId, token),
            functions => slugs.All(slug => functions.Any(function => function.Slug == slug)),
            timeout ?? DefaultTimeout,
            _describe,
            cancellationToken);

    public async Task<IReadOnlyList<RegisteredApp>> GetAppsAsync(CancellationToken cancellationToken)
    {
        using var body = await QueryAsync(AppsQuery, variables: null, cancellationToken);
        return [.. body.RootElement.GetProperty("data").GetProperty("apps").EnumerateArray().Select(RegisteredApp.FromJson)];
    }

    // Registers an app the way an SDK does when it is synced. The harness's fake SDK uses it.
    public async Task RegisterAsync(string registration, CancellationToken cancellationToken)
    {
        using var content = new StringContent(registration, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync("fn/register", content, cancellationToken);
        await ReadSuccessAsync(response, cancellationToken);
    }

    public async Task RemoveAppAsync(Uri appUrl, CancellationToken cancellationToken)
    {
        using var response = await _http.DeleteAsync($"fn/remove?url={Uri.EscapeDataString(appUrl.AbsoluteUri)}", cancellationToken);
        await ReadSuccessAsync(response, cancellationToken);
    }

    public async Task<JsonDocument> QueryAsync(string query, object? variables, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync("v0/gql", new { query, variables }, JsonSerializerOptions.Web, cancellationToken);
        var body = await ReadJsonAsync(response, cancellationToken);
        if (body.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            var text = errors.GetRawText();
            body.Dispose();
            throw new GraphQLException(text);
        }

        return body;
    }

    public void Dispose() => _http.Dispose();

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync($"{path}?cb={Interlocked.Increment(ref _requestCounter)}", cancellationToken);
        return await ReadJsonAsync(response, cancellationToken);
    }

    // Several endpoints answer JSON as text/plain, so the body is parsed whatever its content type.
    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        JsonDocument.Parse(await ReadSuccessAsync(response, cancellationToken));

    private static async Task<string> ReadSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return response.IsSuccessStatusCode
            ? text
            : throw new InvalidOperationException(
                $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery} returned {(int)response.StatusCode}: {text}");
    }
}

public sealed record EventPayload(string Name, object Data);

public sealed class GraphQLException(string errors) : Exception($"The GraphQL query failed: {errors}")
{
    // The "errors" array of the response, as JSON.
    public string Errors { get; } = errors;
}
