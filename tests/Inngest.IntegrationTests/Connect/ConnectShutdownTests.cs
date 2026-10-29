using Inngest.IntegrationTests.Functions;
using Inngest.IntegrationTests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Inngest.IntegrationTests.Connect;

// A worker that stops while it runs an execution finishes it, replies, and only then disconnects, which the server
// records as a worker shutdown rather than a lost connection.
[Collection(ConnectCollection.Name)]
public sealed class ConnectShutdownTests(ConnectFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private readonly string _testId = Guid.NewGuid().ToString("N");
    private readonly SlowFunction.Gate _gate = new();
    private ConnectWorkerApp? _worker;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        // A worker of its own, for an app of its own, so that stopping it leaves the shared worker alone.
        _worker = await ConnectWorkerApp.StartAsync(
            fixture.ServerUrl,
            SlowFunction.AppId,
            inngest => inngest.AddFunction<SlowFunction>(SlowFunction.Options),
            Cancellation,
            services => services.AddSingleton(_gate));
        await fixture.Api.WaitForFunctionsAsync(SlowFunction.AppId, [SlowFunction.Slug], Cancellation);
    }

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

        _gate.Released.TrySetResult();
        if (_worker is not null)
        {
            await _worker.DisposeAsync();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Finishes_the_running_execution_before_it_disconnects()
    {
        var worker = _worker!;
        var eventId = await fixture.Api.SendEventAsync("itests/connect.slow", new TestEvent(_testId), Cancellation);
        await _gate.Entered.Task.WaitAsync(Wait, Cancellation);

        (await fixture.GetLiveInstancesAsync(Cancellation)).ShouldContain(worker.InstanceId);
        var stopping = worker.StopAsync(Cancellation);
        await Task.Delay(TimeSpan.FromSeconds(1), Cancellation);
        stopping.IsCompleted.ShouldBeFalse();
        _gate.Released.SetResult();
        await stopping.WaitAsync(Wait, Cancellation);

        var run = await fixture.Api.WaitForRunEndAsync(eventId, Cancellation);
        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<TextResult>().ShouldBe(new TextResult("finished"));
        worker.Warnings().ShouldBeEmpty();
        worker.Logs.GetSnapshot().ShouldContain(record => record.Id.Id == 510 && record.Level == LogLevel.Information);

        // The gateway has let go of the connection, after one connection for the whole life of the worker.
        await Poll.UntilAsync(
            "the gateway to drop the worker's connection",
            token => fixture.GetLiveInstancesAsync(token),
            instances => !instances.Contains(worker.InstanceId),
            Wait,
            fixture.DescribeAsync,
            Cancellation);
        var connection = (await fixture.GetConnectionsAsync(worker.InstanceId, Cancellation)).ShouldHaveSingleItem();

        // The gateway records the end of a connection twice, as DISCONNECTING and as DISCONNECTED with the close
        // reason, and the first write runs on its own goroutine, so either can be the one that stays. The reason,
        // when it stays, must be the one a worker that shuts down gives.
        connection.Status.ShouldBeOneOf("DISCONNECTING", "DISCONNECTED");
        if (connection.Status == "DISCONNECTED")
        {
            connection.DisconnectReason.ShouldBe("WORKER_SHUTDOWN");
        }
    }
}
