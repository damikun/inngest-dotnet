using Inngest.Execution;
using Inngest.Functions;
using Inngest.Tests.Checkpointing;
using Inngest.Tests.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Tests.UnitOfWork;

// What a DbContext is to a step, without EF Core: a scoped service that holds state, is made once per scope and is
// disposed with it. The log keeps every instance in the order they were made, and when each one was made and disposed.
internal sealed class ScopeLog
{
    private readonly Lock _gate = new();
    private readonly List<ScopedProbe> _created = [];
    private readonly List<string> _timeline = [];

    public IReadOnlyList<ScopedProbe> Created
    {
        get
        {
            lock (_gate)
            {
                return [.. _created];
            }
        }
    }

    public IReadOnlyList<string> Timeline
    {
        get
        {
            lock (_gate)
            {
                return [.. _timeline];
            }
        }
    }

    public int Add(ScopedProbe probe)
    {
        lock (_gate)
        {
            _created.Add(probe);
            _timeline.Add($"create {_created.Count}");
            return _created.Count;
        }
    }

    public void Disposed(ScopedProbe probe)
    {
        lock (_gate)
        {
            _timeline.Add($"dispose {probe.Number}");
        }
    }
}

internal abstract class ScopedProbe
{
    protected ScopedProbe(ScopeLog log)
    {
        Log = log;
        Number = log.Add(this);
    }

    // The tracked entities of the stand-in: whatever a step puts here stays in this instance only.
    public List<string> Entries { get; } = [];

    // 1 for the first instance made, in the order the log saw them.
    public int Number { get; }

    public bool IsDisposed { get; protected set; }

    protected ScopeLog Log { get; }
}

internal sealed class Probe(ScopeLog log) : ScopedProbe(log), IDisposable
{
    public void Dispose()
    {
        IsDisposed = true;
        Log.Disposed(this);
    }
}

// Disposable only asynchronously, as a DbContext may be.
internal sealed class AsyncProbe(ScopeLog log) : ScopedProbe(log), IAsyncDisposable
{
    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        Log.Disposed(this);
        return ValueTask.CompletedTask;
    }
}

internal sealed class ThrowingProbe(ScopeLog log) : ScopedProbe(log), IDisposable
{
    public void Dispose()
    {
        IsDisposed = true;
        Log.Disposed(this);
        throw new InvalidOperationException("disposal failed");
    }
}

internal static class ScopedServices
{
    public static IServiceCollection AddProbes(this IServiceCollection services, ScopeLog log) =>
        services.AddSingleton(log).AddScoped<Probe>().AddScoped<AsyncProbe>().AddScoped<ThrowingProbe>();

    // Calls the function the way the serve endpoint does: from a scope made for the call, and disposed once the call has
    // answered. EngineHost alone hands the engine its root provider, which has no scope to tell from a step's.
    public static async Task<CallResult> CallInScopeAsync(this EngineHost host, FunctionDefinition function, Call call, string? stepId = null)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var request = new ExecutionRequest
        {
            Function = function,
            Body = call.ToBytes(),
            StepId = stepId ?? "step",
            Services = scope.ServiceProvider,
        };
        return await EngineHost.ResultOf(host.Engine.ExecuteAsync(request, TestContext.Current.CancellationToken));
    }

    // The same for a function with checkpointing on.
    public static async Task<CallResult> CallCheckpointedInScopeAsync(this EngineHost host, FunctionDefinition function, Call call)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var request = Checkpointed.Request(host, function, call) with { Services = scope.ServiceProvider };
        return await EngineHost.ResultOf(host.Start(request));
    }
}
