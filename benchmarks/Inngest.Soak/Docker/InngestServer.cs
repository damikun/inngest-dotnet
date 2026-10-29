using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Inngest.Soak.Docker;

internal sealed record RunInfo(string Id, string Status, string? Output, DateTimeOffset? QueuedAt, DateTimeOffset? EndedAt, string FunctionSlug, string? TriggerId = null)
{
    public bool IsFinished => EndedAt is not null && Status is "COMPLETED" or "FAILED" or "CANCELLED";

    public static RunInfo Read(JsonElement node) => new(
        node.GetProperty("id").GetString()!,
        node.GetProperty("status").GetString()!,
        node.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.String ? output.GetString() : null,
        Time(node, "queuedAt"),
        Time(node, "endedAt"),
        node.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object ? function.GetProperty("slug").GetString()! : string.Empty,
        node.TryGetProperty("triggerIDs", out var triggers) && triggers.ValueKind == JsonValueKind.Array && triggers.GetArrayLength() > 0 ? triggers[0].GetString() : null);

    private static DateTimeOffset? Time(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetDateTimeOffset() : null;
}

// The event API and the GraphQL API of an Inngest server, as a test harness uses them. The REST /v1 endpoints are
// avoided because the server caches their answers.
internal sealed class InngestServer(Uri baseUrl, string eventKey) : IDisposable
{
    private static readonly string[] Finished = ["COMPLETED", "FAILED", "CANCELLED"];

    private readonly HttpClient _http = new() { BaseAddress = baseUrl, Timeout = TimeSpan.FromSeconds(60) };

    public Uri BaseUrl => baseUrl;

    public void Dispose() => _http.Dispose();

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync("health", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    // Returns the ids Inngest gave the events, in order.
    public async Task<string[]> SendAsync(IReadOnlyList<object> events, CancellationToken cancellationToken, string? key = null)
    {
        using var response = await _http.PostAsJsonAsync($"e/{Uri.EscapeDataString(key ?? eventKey)}", events, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var reply = JsonDocument.Parse(body);
        if (!response.IsSuccessStatusCode || reply.RootElement.GetProperty("status").GetInt32() != 200)
        {
            throw new InngestServerException((int)response.StatusCode, $"Sending events failed with {(int)response.StatusCode}: {body}");
        }

        return [.. reply.RootElement.GetProperty("ids").EnumerateArray().Select(static id => id.GetString()!)];
    }

    public async Task<JsonElement> QueryAsync(string query, object? variables, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync("v0/gql", new { query, variables }, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InngestServerException((int)response.StatusCode, $"GraphQL answered {(int)response.StatusCode}: {body}");
        }

        using var document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            throw new InngestServerException(200, $"GraphQL errors: {errors.GetRawText()}");
        }

        return document.RootElement.GetProperty("data").Clone();
    }

    public async Task<JsonElement?> FindAppAsync(string appId, CancellationToken cancellationToken)
    {
        var data = await QueryAsync(
            "{ apps { id externalID name sdkLanguage sdkVersion framework url error method connected functionCount functions { slug } } }",
            null,
            cancellationToken);
        foreach (var app in data.GetProperty("apps").EnumerateArray())
        {
            if (app.GetProperty("externalID").GetString() == appId)
            {
                return app;
            }
        }

        return null;
    }

    public async Task<Dictionary<string, string>> FunctionIdsAsync(CancellationToken cancellationToken)
    {
        var data = await QueryAsync("{ functions { id slug } }", null, cancellationToken);
        return data.GetProperty("functions").EnumerateArray().ToDictionary(
            static function => function.GetProperty("slug").GetString()!,
            static function => function.GetProperty("id").GetString()!,
            StringComparer.Ordinal);
    }

    // Empty until the server has stored the event, which takes a moment after the event API accepted it.
    public async Task<RunInfo[]> EventRunsAsync(string eventId, CancellationToken cancellationToken)
    {
        JsonElement data;
        try
        {
            data = await QueryAsync(
                "query($id: ULID!) { eventV2(id: $id) { runs { id status output queuedAt endedAt function { slug } } } }",
                new { id = eventId },
                cancellationToken);
        }
        catch (InngestServerException exception) when (exception.Message.Contains("event not found", StringComparison.Ordinal))
        {
            return [];
        }

        return data.GetProperty("eventV2") is { ValueKind: JsonValueKind.Object } found
            ? [.. found.GetProperty("runs").EnumerateArray().Select(RunInfo.Read)]
            : [];
    }

    public async Task<(RunInfo Run, JsonElement Spans)> RunAsync(string runId, CancellationToken cancellationToken)
    {
        var data = await QueryAsync(
            "query($runID: String!) { run(runID: $runID) { id status output queuedAt endedAt function { slug } trace { childrenSpans { name status stepOp } } } }",
            new { runID = runId },
            cancellationToken);
        var run = data.GetProperty("run");
        var spans = run.TryGetProperty("trace", out var trace) && trace.ValueKind == JsonValueKind.Object ? trace.GetProperty("childrenSpans") : default;
        return (RunInfo.Read(run), spans);
    }

    // A run's status and output from the run itself. The run query above reads the trace, which the server can fall
    // behind on writing under load; this one reads the run.
    public async Task<RunInfo> FunctionRunAsync(string runId, CancellationToken cancellationToken)
    {
        var data = await QueryAsync(
            "query($id: ID!) { functionRun(query: {functionRunId: $id}) { id status output startedAt finishedAt function { slug } } }",
            new { id = runId },
            cancellationToken);
        var run = data.GetProperty("functionRun");
        return new RunInfo(
            run.GetProperty("id").GetString()!,
            run.GetProperty("status").GetString()!,
            run.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.String ? output.GetString() : null,
            run.TryGetProperty("startedAt", out var started) && started.ValueKind == JsonValueKind.String ? started.GetDateTimeOffset() : null,
            run.TryGetProperty("finishedAt", out var finished) && finished.ValueKind == JsonValueKind.String ? finished.GetDateTimeOffset() : null,
            run.GetProperty("function").GetProperty("slug").GetString()!);
    }

    // Finished runs of the functions queued since the given time.
    public async Task<int> CountFinishedAsync(DateTimeOffset from, IReadOnlyList<string> functionIds, CancellationToken cancellationToken)
    {
        var data = await QueryAsync(
            "query($from: Time!, $functions: [UUID!], $status: [FunctionRunStatus!]) { runs(first: 1, orderBy: [{field: QUEUED_AT, direction: ASC}], filter: {from: $from, functionIDs: $functions, status: $status}) { totalCount } }",
            new { from, functions = functionIds, status = Finished },
            cancellationToken);
        return data.GetProperty("runs").GetProperty("totalCount").GetInt32();
    }

    // Every run of the functions queued since the given time, a page at a time. Outputs are not part of the list.
    public async IAsyncEnumerable<RunInfo> ListRunsAsync(DateTimeOffset from, IReadOnlyList<string> functionIds, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? cursor = null;
        while (true)
        {
            var data = await QueryAsync(
                "query($from: Time!, $functions: [UUID!], $after: String) { runs(first: 200, after: $after, orderBy: [{field: QUEUED_AT, direction: ASC}], filter: {from: $from, functionIDs: $functions}) { edges { node { id status queuedAt endedAt triggerIDs function { slug } } } pageInfo { hasNextPage endCursor } } }",
                new { from, functions = functionIds, after = cursor },
                cancellationToken);
            var runs = data.GetProperty("runs");
            foreach (var edge in runs.GetProperty("edges").EnumerateArray())
            {
                yield return RunInfo.Read(edge.GetProperty("node"));
            }

            var page = runs.GetProperty("pageInfo");
            if (!page.GetProperty("hasNextPage").GetBoolean())
            {
                yield break;
            }

            cursor = page.GetProperty("endCursor").GetString();
        }
    }
}

internal sealed class InngestServerException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
