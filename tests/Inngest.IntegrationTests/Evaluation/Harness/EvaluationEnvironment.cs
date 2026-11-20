using System.Collections.Concurrent;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.Evaluation.Harness;

// The servers and apps of the evaluation suite. A collection that shares a server starts it and tears the container
// down when that collection finishes, so finished servers are not still sitting on Docker Desktop's host gateway.
// A scenario outside those collections starts its server on first use; the assembly fixture tears those down at the end.
// A fixture that fails to start fails every scenario that needs it with the same error.
public sealed class EvaluationEnvironment : IAsyncDisposable
{
    private static EvaluationEnvironment? _current;

    private readonly ConcurrentDictionary<Type, Lazy<Task<IEvaluationFixture>>> _fixtures = new();

    public EvaluationEnvironment() => _current = this;

    public async Task<TFixture> GetAsync<TFixture>()
        where TFixture : class, IEvaluationFixture, new()
    {
        var fixture = _fixtures.GetOrAdd(typeof(TFixture), static _ => new Lazy<Task<IEvaluationFixture>>(StartAsync<TFixture>));
        return (TFixture)await fixture.Value;
    }

    // The collection fixture calls this before any of its scenarios, and Release when the collection has finished.
    internal static void Hold(IEvaluationFixture fixture)
    {
        var environment = _current ?? throw new InvalidOperationException("The evaluation environment is not running.");
        if (!environment._fixtures.TryAdd(fixture.GetType(), new Lazy<Task<IEvaluationFixture>>(() => Task.FromResult(fixture))))
        {
            throw new InvalidOperationException($"{fixture.GetType().Name} is already started.");
        }
    }

    internal static async ValueTask Release(IEvaluationFixture fixture)
    {
        var environment = _current ?? throw new InvalidOperationException("The evaluation environment is not running.");
        if (!environment._fixtures.TryRemove(fixture.GetType(), out var started))
        {
            return;
        }

        try
        {
            await (await started.Value).DisposeAsync();
        }
        catch (Exception)
        {
            // It failed to start and has cleaned up after itself already.
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var fixture in _fixtures.Values.Where(fixture => fixture.IsValueCreated))
        {
            try
            {
                await (await fixture.Value).DisposeAsync();
            }
            catch (Exception)
            {
                // It failed to start and has cleaned up after itself already.
            }
        }
    }

    private static async Task<IEvaluationFixture> StartAsync<TFixture>()
        where TFixture : class, IEvaluationFixture, new()
    {
        var fixture = new TFixture();
        await fixture.InitializeAsync();
        return fixture;
    }
}

// What a scenario needs from the server and app it runs against.
public interface IEvaluationFixture : IAsyncLifetime
{
    DevServerClient Api { get; }

    // What the functions observed, by test id.
    Probe Probe { get; }

    Task EnsureSyncedAsync(CancellationToken cancellationToken);

    Task<string> DescribeAsync(CancellationToken cancellationToken);
}
