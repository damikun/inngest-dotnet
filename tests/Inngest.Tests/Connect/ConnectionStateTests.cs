using System.Net;
using Inngest.Connect;
using Inngest.Connect.Protocol;
using Inngest.Tests.Connect.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Tests.Connect;

// The state follows the worker: connecting until its first connection is ready, reconnecting while a lost connection
// is replaced, closing from the moment the host stops, and closed once the worker is done.
public sealed class ConnectionStateTests : IAsyncLifetime
{
    private FakeGateway _gateway = null!;

    public async ValueTask InitializeAsync() => _gateway = await FakeGateway.StartAsync();

    public async ValueTask DisposeAsync() => await _gateway.DisposeAsync();

    [Fact]
    public async Task Is_connecting_until_the_first_connection_is_ready()
    {
        await using var worker = new WorkerHost(_gateway);
        var connection = ConnectionOf(worker);
        connection.State.ShouldBe(InngestConnectionState.Connecting);

        await worker.StartAsync();
        await _gateway.NextReadySessionAsync();

        await WaitForAsync(connection, InngestConnectionState.Active);
    }

    [Fact]
    public async Task Stays_connecting_while_the_first_attempts_fail()
    {
        var refusals = 1;
        _gateway.StartStatus = _ => Interlocked.Decrement(ref refusals) >= 0 ? HttpStatusCode.ServiceUnavailable : null;
        await using var worker = new WorkerHost(_gateway);
        var connection = ConnectionOf(worker);

        var backoff = worker.Time.NextTimerAsync(TimeSpan.FromSeconds(1));
        await worker.StartAsync();
        await backoff;

        connection.State.ShouldBe(InngestConnectionState.Connecting);
        worker.Time.Advance(TimeSpan.FromSeconds(1));
        await _gateway.NextReadySessionAsync();
        await WaitForAsync(connection, InngestConnectionState.Active);
    }

    [Theory]
    [InlineData("dropped")]
    [InlineData("drained")]
    public async Task Is_reconnecting_until_a_lost_connection_is_replaced(string loss)
    {
        // The first attempt to replace the connection fails, so the worker waits in its backoff where the test can look.
        var starts = 0;
        _gateway.StartStatus = _ => Interlocked.Increment(ref starts) == 2 ? HttpStatusCode.ServiceUnavailable : null;
        await using var worker = new WorkerHost(_gateway);
        var connection = ConnectionOf(worker);
        await worker.StartAsync();
        var first = await _gateway.NextReadySessionAsync();
        await WaitForAsync(connection, InngestConnectionState.Active);

        var backoff = worker.Time.NextTimerAsync(TimeSpan.FromSeconds(1));
        if (loss == "dropped")
        {
            first.Abort();
        }
        else
        {
            await first.SendAsync(GatewayMessageType.GatewayClosing);
        }

        await backoff;
        connection.State.ShouldBe(InngestConnectionState.Reconnecting);

        worker.Time.Advance(TimeSpan.FromSeconds(1));
        await _gateway.NextReadySessionAsync();
        await WaitForAsync(connection, InngestConnectionState.Active);
    }

    [Fact]
    public async Task Is_closing_while_running_executions_finish_and_closed_after()
    {
        _gateway.HeartbeatInterval = "1h";
        var engine = new ControlledEngine();
        await using var worker = new WorkerHost(_gateway, engine: engine);
        var connection = ConnectionOf(worker);
        var session = await StartRequestAsync(worker);
        var execution = await engine.NextAsync();

        var stopping = worker.StopAsync(TestContext.Current.CancellationToken);
        await session.NextAsync(GatewayMessageType.WorkerPause);
        connection.State.ShouldBe(InngestConnectionState.Closing);

        execution.Complete(200, "null");
        await stopping.WaitAsync(Waits.Timeout);
        connection.State.ShouldBe(InngestConnectionState.Closed);
    }

    [Fact]
    public async Task Stays_closing_when_it_reconnects_to_finish_an_execution()
    {
        _gateway.HeartbeatInterval = "1h";
        var engine = new ControlledEngine();
        await using var worker = new WorkerHost(_gateway, engine: engine);
        var connection = ConnectionOf(worker);
        var session = await StartRequestAsync(worker);
        var execution = await engine.NextAsync();
        var stopping = worker.StopAsync(TestContext.Current.CancellationToken);
        await session.NextAsync(GatewayMessageType.WorkerPause);

        session.Abort();

        // The replacement only keeps the lease alive: it pauses instead of announcing readiness.
        var replacement = await _gateway.NextSessionAsync();
        await replacement.NextAsync(GatewayMessageType.WorkerPause);
        connection.State.ShouldBe(InngestConnectionState.Closing);
        execution.Complete(200, "null");
        await stopping.WaitAsync(Waits.Timeout);
        connection.State.ShouldBe(InngestConnectionState.Closed);
    }

    [Fact]
    public void Moves_only_along_the_worker_state_machine()
    {
        var connection = new InngestConnection();

        connection.MoveTo(InngestConnectionState.Reconnecting);
        connection.State.ShouldBe(InngestConnectionState.Connecting);
        connection.MoveTo(InngestConnectionState.Active);
        connection.State.ShouldBe(InngestConnectionState.Active);
        connection.MoveTo(InngestConnectionState.Connecting);
        connection.State.ShouldBe(InngestConnectionState.Active);
        connection.MoveTo(InngestConnectionState.Reconnecting);
        connection.State.ShouldBe(InngestConnectionState.Reconnecting);
        connection.MoveTo(InngestConnectionState.Closing);
        connection.MoveTo(InngestConnectionState.Active);
        connection.MoveTo(InngestConnectionState.Reconnecting);
        connection.State.ShouldBe(InngestConnectionState.Closing);
        connection.MoveTo(InngestConnectionState.Closed);
        connection.MoveTo(InngestConnectionState.Active);
        connection.State.ShouldBe(InngestConnectionState.Closed);
    }

    [Fact]
    public async Task A_wait_returns_at_once_when_the_state_has_moved_on()
    {
        var connection = new InngestConnection();
        connection.MoveTo(InngestConnectionState.Active);

        var wait = connection.WaitForStateChangedAsync(InngestConnectionState.Connecting, TestContext.Current.CancellationToken);

        wait.IsCompletedSuccessfully.ShouldBeTrue();
        (await wait).ShouldBe(InngestConnectionState.Active);
    }

    [Fact]
    public async Task A_wait_ends_when_its_token_is_cancelled()
    {
        var connection = new InngestConnection();
        using var cancellation = new CancellationTokenSource();

        var wait = connection.WaitForStateChangedAsync(InngestConnectionState.Connecting, cancellation.Token);
        wait.IsCompleted.ShouldBeFalse();
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(wait);
    }

    [Fact]
    public async Task Every_waiter_sees_a_change_that_races_with_the_start_of_its_wait()
    {
        var connection = new InngestConnection();
        using var start = new ManualResetEventSlim();
        var waits = Enumerable.Range(0, 64)
            .Select(_ => Task.Run(
                () =>
                {
                    start.Wait(TestContext.Current.CancellationToken);
                    return connection.WaitForStateChangedAsync(InngestConnectionState.Connecting, TestContext.Current.CancellationToken);
                },
                TestContext.Current.CancellationToken))
            .ToArray();

        start.Set();
        connection.MoveTo(InngestConnectionState.Active);

        (await Task.WhenAll(waits).WaitAsync(Waits.Timeout, TestContext.Current.CancellationToken))
            .ShouldAllBe(state => state == InngestConnectionState.Active);
    }

    private static IInngestConnection ConnectionOf(WorkerHost worker) => worker.Services.GetRequiredService<IInngestConnection>();

    private async Task<GatewaySession> StartRequestAsync(WorkerHost worker)
    {
        await worker.StartAsync();
        var session = await _gateway.NextReadySessionAsync();
        await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, GatewayMessages.Request("req-1"));
        await session.NextAsync(GatewayMessageType.WorkerRequestAck);
        return session;
    }

    private static async Task WaitForAsync(IInngestConnection connection, InngestConnectionState state)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(Waits.Timeout);
        var current = connection.State;
        while (current != state)
        {
            current = await connection.WaitForStateChangedAsync(current, timeout.Token);
        }
    }
}
