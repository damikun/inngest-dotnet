using System.Text;
using System.Text.Json;
using Inngest.Execution;
using Inngest.Internal;
using Inngest.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Tests.Testing;

internal static class TestHosts
{
    public const string AppId = "test-app";
    public const string EventName = "test/event";

    public static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public static FunctionOptions Options(string id = "fn", int? retries = null) =>
        new() { Id = id, Triggers = [Trigger.Event(EventName)], Retries = retries };

    public static InngestEvent Event(object? data = null) => new(EventName, data);

    // A host with one delegate function, "fn". configure adds services and middleware.
    public static InngestTestHost For(
        Func<FunctionContext<JsonElement>, Task<object?>> handler,
        int? retries = null,
        Action<IInngestBuilder>? configure = null) =>
        new(Services(handler, retries, configure));

    public static ServiceCollection Services(
        Func<FunctionContext<JsonElement>, Task<object?>> handler,
        int? retries = null,
        Action<IInngestBuilder>? configure = null)
    {
        var services = new ServiceCollection();
        var inngest = services.AddInngest(options => options.AppId = AppId).AddFunction(Options(retries: retries), (context, _) => handler(context));
        configure?.Invoke(inngest);
        return services;
    }
}

// Wraps the real engine and keeps what the host sent it.
internal sealed class RecordingEngine(IExecutionEngine inner) : IExecutionEngine
{
    private readonly List<(string StepId, JsonElement Body)> _calls = [];

    // The stepId of each call: "step", or the hashed id of the planned step to run.
    public IReadOnlyList<string> Targets
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls.Select(call => call.StepId)];
            }
        }
    }

    public IReadOnlyList<JsonElement> Bodies
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls.Select(call => call.Body)];
            }
        }
    }

    public Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken)
    {
        using var body = JsonDocument.Parse(request.Body);
        lock (_calls)
        {
            _calls.Add((request.StepId!, body.RootElement.Clone()));
        }

        return inner.ExecuteAsync(request, cancellationToken);
    }
}

// Answers calls with canned responses, for ops the engine does not produce yet.
internal sealed class ScriptedEngine(params (int Status, string Body)[] answers) : IExecutionEngine
{
    private readonly List<(string StepId, JsonElement Body)> _calls = [];

    public IReadOnlyList<string> Targets => [.. _calls.Select(call => call.StepId)];

    public IReadOnlyList<JsonElement> Bodies => [.. _calls.Select(call => call.Body)];

    public Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken)
    {
        using var body = JsonDocument.Parse(request.Body);
        _calls.Add((request.StepId!, body.RootElement.Clone()));
        if (_calls.Count > answers.Length)
        {
            throw new InvalidOperationException($"Call {_calls.Count} was not expected.");
        }

        var (status, json) = answers[_calls.Count - 1];
        var buffer = new PooledBufferWriter();
        buffer.Write(Encoding.UTF8.GetBytes(json));
        return Task.FromResult(new ExecutionResult(status, buffer) { NoRetry = status switch { 400 => true, 500 => false, _ => null } });
    }

    // An op as the SDK writes it, for steps it does not know yet.
    public static string Op(string id, string op, string opts) =>
        $$$"""{"id":"{{{StepIds.Hash(id)}}}","op":"{{{op}}}","displayName":"{{{id}}}","opts":{{{opts}}},"userland":{"id":"{{{id}}}"}}""";
}
