using System.Collections.Concurrent;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.Evaluation.Harness;

// The servers and apps of the evaluation suite. Each fixture starts the first time a scenario asks for it, so a run of
// the other integration tests never starts them, and is shared from then on, so scenario classes run in parallel
// against it. A fixture that fails to start fails every scenario that needs it with the same error.
public sealed class EvaluationEnvironment : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Type, Lazy<Task<IEvaluationFixture>>> _fixtures = new();

    public async Task<TFixture> GetAsync<TFixture>()
        where TFixture : class, IEvaluationFixture, new()
    {
        var fixture = _fixtures.GetOrAdd(typeof(TFixture), static _ => new Lazy<Task<IEvaluationFixture>>(StartAsync<TFixture>));
        return (TFixture)await fixture.Value;
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
