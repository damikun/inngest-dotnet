using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Inngest.IntegrationTests.Connect;

// A worker reports its connection to the dev server's gateway as active, and the readiness check that a Kubernetes
// probe would call follows it: ready while the worker is connected, not ready once it has stopped.
[Collection(ConnectCollection.Name)]
public sealed class ConnectReadinessTests(ConnectFixture fixture) : IAsyncLifetime
{
    private const string AppId = "itests-connect-readiness";
    private const string Slug = "itests-connect-readiness-ready";

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private ConnectWorkerApp? _worker;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        _worker = await ConnectWorkerApp.StartAsync(
            fixture.ServerUrl,
            AppId,
            inngest => inngest.AddFunction(
                new FunctionOptions { Id = "ready", Triggers = [Trigger.Event("itests/connect.ready")] },
                static (_, _) => Task.FromResult<object?>(null)),
            Cancellation,
            services => services.AddHealthChecks().AddInngestConnect(tags: ["ready"]));

    public async ValueTask DisposeAsync()
    {
        if (TestContext.Current.TestState?.Result == TestResult.Failed && TestContext.Current.TestOutputHelper is { } output)
        {
            foreach (var record in _worker?.Logs.GetSnapshot() ?? [])
            {
                output.WriteLine($"{record.Timestamp:HH:mm:ss.fff} {record.Level} {record.Category}: {record.Message} {record.Exception}");
            }

            output.WriteLine(await fixture.DescribeAsync(CancellationToken.None));
        }

        if (_worker is not null)
        {
            await _worker.DisposeAsync();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Is_active_and_ready_while_connected_and_not_ready_once_stopped()
    {
        var worker = _worker!;
        var connection = worker.Services.GetRequiredService<IInngestConnection>();
        var health = worker.Services.GetRequiredService<HealthCheckService>();

        await WaitForAsync(connection, InngestConnectionState.Active);
        await fixture.Api.WaitForFunctionsAsync(AppId, [Slug], Cancellation);
        var ready = await health.CheckHealthAsync(static check => check.Tags.Contains("ready"), Cancellation);

        ready.Status.ShouldBe(HealthStatus.Healthy);
        ready.Entries["inngest-connect"].Data["state"].ShouldBe("Active");
        (await fixture.GetLiveInstancesAsync(Cancellation)).ShouldContain(worker.InstanceId);

        await worker.StopAsync(Cancellation);

        connection.State.ShouldBe(InngestConnectionState.Closed);
        var stopped = await health.CheckHealthAsync(static check => check.Tags.Contains("ready"), Cancellation);
        stopped.Status.ShouldBe(HealthStatus.Unhealthy);
        stopped.Entries["inngest-connect"].Data["state"].ShouldBe("Closed");
    }

    private static async Task WaitForAsync(IInngestConnection connection, InngestConnectionState state)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        timeout.CancelAfter(Wait);
        var current = connection.State;
        while (current != state)
        {
            current = await connection.WaitForStateChangedAsync(current, timeout.Token);
        }
    }
}
