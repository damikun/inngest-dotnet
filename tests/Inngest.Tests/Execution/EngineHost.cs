using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Inngest.Client;
using Inngest.Configuration;
using Inngest.Execution;
using Inngest.Functions;
using Inngest.Tests.Checkpointing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Inngest.Tests.Execution;

// Runs the real engine in memory: fake time, fake logging, a fake Inngest API and whatever services a test adds.
internal sealed class EngineHost : IDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 10, 29, 10, 0, 0, TimeSpan.Zero);

    // Now in Unix nanoseconds, as the "a" of a step's timing.
    public const long NowNanoseconds = 1793268000000000000;

    private readonly ServiceProvider _provider;

    public EngineHost(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddFakeLogging();
        configure?.Invoke(services);
        _provider = services.BuildServiceProvider();
        Logs = _provider.GetFakeLogCollector();
        Engine = new ExecutionEngine(Settings, _provider.GetRequiredService<ILoggerFactory>(), Time, Api, Checkpoints);
    }

    public static InngestSettings Settings { get; } = new()
    {
        AppId = "test-app",
        Mode = InngestMode.Dev,
        ApiBaseUrl = new Uri("http://localhost:8288"),
        EventApiBaseUrl = new Uri("http://localhost:8288"),
        SerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() },
    };

    public TestTimeProvider Time { get; } = new(Now);

    public FakeInngestApi Api { get; } = new();

    public FakeCheckpointApi Checkpoints { get; } = new();

    public FakeLogCollector Logs { get; }

    public ExecutionEngine Engine { get; }

    public IServiceProvider Services => _provider;

    public async Task<CallResult> CallAsync(FunctionDefinition function, Call call, string? stepId = null) =>
        await ResultOf(Start(function, call, stepId));

    // The result of a call that a test started with Start and finishes by moving the clock on.
    public static async Task<CallResult> ResultOf(Task<ExecutionResult> pending)
    {
        using var result = await pending;
        return new CallResult(result.StatusCode, Encoding.UTF8.GetString(result.Body.WrittenSpan), result.NoRetry, result.RetryAfter);
    }

    // Runs the engine up to its first real await, which is where it waits for the function's own work.
    public Task<ExecutionResult> Start(FunctionDefinition function, Call call, string? stepId = null) =>
        Engine.ExecuteAsync(
            new ExecutionRequest { Function = function, Body = call.ToBytes(), StepId = stepId ?? "step", Services = _provider },
            TestContext.Current.CancellationToken);

    public IReadOnlyList<FakeLogRecord> LogsOf(string category) =>
        [.. Logs.GetSnapshot().Where(record => record.Category == category)];

    public void Dispose() => _provider.Dispose();
}

// Fake time that also tells a test when code has started waiting on it.
internal sealed class TestTimeProvider(DateTimeOffset start) : FakeTimeProvider(start)
{
    private readonly Lock _gate = new();
    private readonly List<(int Count, TaskCompletionSource Reached)> _waiters = [];
    private int _timers;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        lock (_gate)
        {
            _timers++;
            foreach (var waiter in _waiters.Where(waiter => waiter.Count <= _timers))
            {
                waiter.Reached.TrySetResult();
            }
        }

        return timer;
    }

    // Completes once this many timers have been created in total.
    public Task TimersCreated(int count)
    {
        lock (_gate)
        {
            if (_timers >= count)
            {
                return Task.CompletedTask;
            }

            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((count, reached));
            return reached.Task;
        }
    }
}

internal static class TestFunction
{
    public static FunctionDefinition Define(Func<FunctionContext<JsonElement>, Task<object?>> handler, string id = "fn") =>
        Define<JsonElement>(handler, id);

    public static FunctionDefinition Define<TData>(Func<FunctionContext<TData>, Task<object?>> handler, string id = "fn") =>
        new(
            new FunctionOptions { Id = id, Triggers = [Trigger.Event("test/event")] },
            _ => new DelegateFunction<TData>((context, _) => handler(context)));
}

// A call body shaped the way Inngest sends it: keys of "steps" sorted and the stack in completion order.
internal sealed class Call
{
    private readonly SortedDictionary<string, string> _steps = new(StringComparer.Ordinal);
    private readonly List<string> _stack = [];
    private string _event = """{"data":{"value":1},"id":"01JTEST","name":"test/event","ts":1793268000000,"user":{}}""";
    private string? _events;
    private string[]? _explicitStack;
    private int _attempt;
    private int? _maxAttempts = 4;
    private bool _disableImmediateExecution;
    private bool _useApi;

    public static Call New() => new();

    public static string Hash(string stepId) => StepIds.Hash(stepId);

    public Call Data(string stepId, string json) => Memo(stepId, $$$"""{"data":{{{json}}}}""");

    public Call Failed(string stepId, string name, string message) =>
        Memo(stepId, $$$"""{"error":{"message":"{{{message}}}","name":"{{{name}}}","stack":"at Somewhere()"}}""");

    // A raw memoized value, such as null for a finished sleep or the event a wait matched.
    public Call Memo(string stepId, string rawJson)
    {
        var hash = Hash(stepId);
        _steps[hash] = rawJson;
        _stack.Add(hash);
        return this;
    }

    // Edited input for a step that has not finished; Inngest sends these outside the stack.
    public Call Input(string stepId, string json)
    {
        _steps[Hash(stepId)] = $$"""{"input":{{json}}}""";
        return this;
    }

    public Call Stack(params string[] stepIds)
    {
        _explicitStack = [.. stepIds.Select(Hash)];
        return this;
    }

    public Call Attempt(int attempt, int? maxAttempts = 4)
    {
        _attempt = attempt;
        _maxAttempts = maxAttempts;
        return this;
    }

    public Call DisableImmediateExecution()
    {
        _disableImmediateExecution = true;
        return this;
    }

    public Call UseApi()
    {
        _useApi = true;
        return this;
    }

    public Call Event(string json)
    {
        _event = json;
        return this;
    }

    public Call Events(params string[] events)
    {
        _events = "[" + string.Join(",", events) + "]";
        return this;
    }

    public byte[] ToBytes()
    {
        var stack = _explicitStack ?? [.. _stack];
        var steps = _useApi ? "{}" : "{" + string.Join(",", _steps.Select(step => $"\"{step.Key}\":{step.Value}")) + "}";
        var events = _useApi ? "[]" : _events ?? "[" + _event + "]";
        var json =
            $$$"""
            {"ctx":{"attempt":{{{_attempt}}},"disable_immediate_execution":{{{Bool(_disableImmediateExecution)}}},"fn_id":"0b3c7d2a-uuid",{{{MaxAttempts()}}}"run_id":"01JRUN","stack":{"current":{{{stack.Length}}},"stack":[{{{string.Join(",", stack.Select(id => $"\"{id}\""))}}}]},"use_api":{{{Bool(_useApi)}}}},"event":{{{_event}}},"events":{{{events}}},"steps":{{{steps}}},"use_api":{{{Bool(_useApi)}}},"version":2}
            """;
        return Encoding.UTF8.GetBytes(json);
    }

    // What the API returns for a run whose state did not fit in the call.
    public string StepsJson() => "{" + string.Join(",", _steps.Select(step => $"\"{step.Key}\":{step.Value}")) + "}";

    public string EventsJson() => _events ?? "[" + _event + "]";

    private string MaxAttempts() => _maxAttempts is { } max ? $"\"max_attempts\":{max}," : string.Empty;

    private static string Bool(bool value) => value ? "true" : "false";
}

internal sealed class CallResult(int statusCode, string body, bool? noRetry, string? retryAfter)
{
    public int StatusCode { get; } = statusCode;

    public string Body { get; } = body;

    public bool? NoRetry { get; } = noRetry;

    public string? RetryAfter { get; } = retryAfter;

    public JsonElement Json => JsonDocument.Parse(Body).RootElement;

    public JsonElement[] Ops => [.. Json.EnumerateArray()];

    public string[] OpCodes => [.. Ops.Select(op => op.GetProperty("op").GetString()!)];

    public string[] DisplayNames => [.. Ops.Select(op => op.GetProperty("displayName").GetString()!)];

    public JsonElement Op(int index = 0) => Ops[index];

    public override string ToString() => $"{StatusCode} {Body}";
}

internal sealed class FakeInngestApi : IInngestApi
{
    public string? Steps { get; set; }

    public string? Events { get; set; }

    public Exception? Failure { get; set; }

    public List<string> Requests { get; } = [];

    public Task<RegisterResult> RegisterAsync(ReadOnlyMemory<byte> payload, string? deployId, string? serverKind, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<JsonDocument> GetRunStepsAsync(string runId, CancellationToken cancellationToken) => Get($"actions:{runId}", Steps);

    public Task<JsonDocument> GetRunEventsAsync(string runId, CancellationToken cancellationToken) => Get($"batch:{runId}", Events);

    private async Task<JsonDocument> Get(string request, string? json)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        await Task.Yield();
        if (Failure is not null)
        {
            throw Failure;
        }

        return JsonDocument.Parse(json ?? throw new InvalidOperationException($"No response for {request}."));
    }
}
