using System.Globalization;
using System.Text.Json;

namespace Inngest.IntegrationTests.Harness;

public enum RunStatus
{
    Unknown,
    Scheduled,
    Running,
    Completed,
    Failed,
    Cancelled,
}

// A function run as the REST API reports it (GET /v1/runs/{id} and /v1/events/{id}/runs). The status is only final
// once EndedAt is set: mid-run the event's run list can already say Completed while a sleep is still pending.
public sealed record RunInfo(
    string RunId,
    string? EventId,
    string? FunctionId,
    RunStatus Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    JsonElement? Output)
{
    public bool HasEnded => EndedAt is not null;

    // A failed run's output is the error the SDK reported.
    public RunError Error => OutputAs<RunError>();

    public T OutputAs<T>() =>
        Output is { } output
            ? output.Deserialize<T>(JsonSerializerOptions.Web) ?? throw new InvalidOperationException($"The output of run {RunId} is null.")
            : throw new InvalidOperationException($"Run {RunId} has no output.");

    public static RunInfo FromJson(JsonElement run) => new(
        run.GetProperty("run_id").GetString()!,
        ServerJson.String(run, "event_id"),
        ServerJson.String(run, "function_id"),
        Enum.TryParse<RunStatus>(ServerJson.String(run, "status"), ignoreCase: true, out var status) ? status : RunStatus.Unknown,
        ServerJson.Time(run, "run_started_at"),
        ServerJson.Time(run, "ended_at"),
        run.TryGetProperty("output", out var output) && output.ValueKind != JsonValueKind.Null ? output.Clone() : null);

    public override string ToString() =>
        $"run {RunId} {Status}{(HasEnded ? " (ended)" : string.Empty)} with output {Output?.GetRawText() ?? "null"}";
}

public sealed record RunError(string? Name, string? Message, string? Stack);

// A run's trace from GraphQL, flattened. Steps show up as spans with a step op (RUN, SLEEP, WAIT_FOR_EVENT, ...) and a
// status; a span only appears once the server has acted on the step, so a WAITING wait-for-event span means its
// pause is stored and a matching event will resume the run.
public sealed record RunTrace(string? Status, IReadOnlyList<TraceSpan> Spans)
{
    public static RunTrace FromJson(JsonElement run)
    {
        var spans = new List<TraceSpan>();
        if (run.TryGetProperty("trace", out var trace) && trace.ValueKind == JsonValueKind.Object)
        {
            AddChildren(trace, spans);
        }

        return new RunTrace(ServerJson.String(run, "status"), spans);
    }

    private static void AddChildren(JsonElement span, List<TraceSpan> spans)
    {
        if (!span.TryGetProperty("childrenSpans", out var children) || children.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var child in children.EnumerateArray())
        {
            spans.Add(new TraceSpan(
                ServerJson.String(child, "name"),
                ServerJson.String(child, "status"),
                ServerJson.String(child, "stepOp"),
                ServerJson.String(child, "stepID")));
            AddChildren(child, spans);
        }
    }

    public override string ToString() => $"run {Status} with spans [{string.Join(", ", Spans)}]";
}

public sealed record TraceSpan(string? Name, string? Status, string? StepOp, string? StepId)
{
    public override string ToString() => $"{Name} {StepOp ?? "-"} {Status}";
}

// Step ops and span statuses as the GraphQL trace spells them.
public static class StepOps
{
    public const string Sleep = "SLEEP";
    public const string WaitForEvent = "WAIT_FOR_EVENT";
}

// A sleeping step is WAITING too.
public static class SpanStatuses
{
    public const string Waiting = "WAITING";
}

// A function as GET /v1/apps/{app}/functions lists it. Config is the configuration the server stored, including
// fields it derives, such as a rate limit for idempotency or a hash for concurrency keys.
public sealed record RegisteredFunction(string InternalId, string Slug, string Name, JsonElement Config)
{
    public JsonElement Step => Config.GetProperty("steps")[0];

    public string StepUrl => Step.GetProperty("uri").GetString()!;

    public int? Retries => Step.TryGetProperty("retries", out var retries) ? retries.GetInt32() : null;

    public static RegisteredFunction FromJson(JsonElement function) => new(
        function.GetProperty("internal_id").GetString()!,
        function.GetProperty("id").GetString()!,
        function.GetProperty("name").GetString()!,
        function.GetProperty("config").Clone());

    public override string ToString() => Slug;
}

// An app as GraphQL lists it. A failing sync shows up here as Error ("unreachable" on a placeholder app with an
// empty name), which the server never logs.
public sealed record RegisteredApp(
    string? ExternalId,
    string? Url,
    string? Error,
    int FunctionCount,
    string? SdkLanguage,
    string? SdkVersion,
    string? Framework)
{
    public static RegisteredApp FromJson(JsonElement app) => new(
        ServerJson.String(app, "externalID"),
        ServerJson.String(app, "url"),
        ServerJson.String(app, "error"),
        app.TryGetProperty("functionCount", out var count) && count.ValueKind == JsonValueKind.Number ? count.GetInt32() : 0,
        ServerJson.String(app, "sdkLanguage"),
        ServerJson.String(app, "sdkVersion"),
        ServerJson.String(app, "framework"));
}

internal static class ServerJson
{
    public static string? String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // The server writes nanosecond fractions, which DateTimeOffset parses down to its 100 ns resolution.
    public static DateTimeOffset? Time(JsonElement element, string property) =>
        String(element, property) is { Length: > 0 } text
            ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)
            : null;
}
