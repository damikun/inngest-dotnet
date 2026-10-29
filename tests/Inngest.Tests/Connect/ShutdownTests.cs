using System.Net.WebSockets;
using Inngest.Connect.Protocol;
using Inngest.Tests.Connect.Support;

namespace Inngest.Tests.Connect;

// When the host stops, the worker pauses its connection so that no new work arrives, lets the executions it holds
// finish and reply, and then closes with 1000 WORKER_SHUTDOWN.
public sealed class ShutdownTests : IAsyncLifetime
{
    private readonly ControlledEngine _engine = new();
    private FakeGateway _gateway = null!;

    public async ValueTask InitializeAsync()
    {
        _gateway = await FakeGateway.StartAsync();
        _gateway.HeartbeatInterval = "1h";
    }

    public async ValueTask DisposeAsync() => await _gateway.DisposeAsync();

    [Fact]
    public async Task Pauses_finishes_the_running_execution_and_then_closes()
    {
        await using var worker = new WorkerHost(_gateway, engine: _engine);
        var session = await StartRequestAsync(worker);
        var execution = await _engine.NextAsync();

        var stopping = worker.StopAsync(TestContext.Current.CancellationToken);

        await session.NextAsync(GatewayMessageType.WorkerPause);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        stopping.IsCompleted.ShouldBeFalse();
        session.Closed.IsCompleted.ShouldBeFalse();
        execution.CancellationToken.IsCancellationRequested.ShouldBeFalse();

        execution.Complete(200, "\"done\"");

        (await session.NextAsync(GatewayMessageType.WorkerReply, SDKResponse.Parser)).RequestId.ShouldBe("req-1");
        await stopping.WaitAsync(Waits.Timeout);
        session.CloseStatus.ShouldBe(WebSocketCloseStatus.NormalClosure);
        session.CloseDescription.ShouldBe("WORKER_SHUTDOWN");
        session.Received.Select(message => message.Kind).TakeLast(2).ShouldBe([GatewayMessageType.WorkerPause, GatewayMessageType.WorkerReply]);
        worker.LogsWithId(510).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Closes_right_after_the_pause_when_nothing_is_running()
    {
        await using var worker = new WorkerHost(_gateway, engine: _engine);
        await worker.StartAsync();
        var session = await _gateway.NextReadySessionAsync();

        await worker.StopAsync(TestContext.Current.CancellationToken).WaitAsync(Waits.Timeout);

        session.Received[^1].Kind.ShouldBe(GatewayMessageType.WorkerPause);
        await session.Closed.WaitAsync(Waits.Timeout);
        session.CloseStatus.ShouldBe(WebSocketCloseStatus.NormalClosure);
        session.CloseDescription.ShouldBe("WORKER_SHUTDOWN");
    }

    [Fact]
    public async Task Keeps_extending_leases_while_it_waits()
    {
        await using var worker = new WorkerHost(_gateway, engine: _engine);
        var session = await StartRequestAsync(worker);
        var execution = await _engine.NextAsync();
        var stopping = worker.StopAsync(TestContext.Current.CancellationToken);
        await session.NextAsync(GatewayMessageType.WorkerPause);

        worker.Time.Advance(TimeSpan.FromSeconds(30));

        (await session.NextAsync(GatewayMessageType.WorkerRequestExtendLease, WorkerRequestExtendLeaseData.Parser)).RequestId.ShouldBe("req-1");
        execution.Complete(200, "null");
        await stopping.WaitAsync(Waits.Timeout);
    }

    [Fact]
    public async Task Leaves_requests_that_arrive_during_shutdown_to_other_workers()
    {
        await using var worker = new WorkerHost(_gateway, engine: _engine);
        var session = await StartRequestAsync(worker);
        var execution = await _engine.NextAsync();
        var stopping = worker.StopAsync(TestContext.Current.CancellationToken);
        await session.NextAsync(GatewayMessageType.WorkerPause);

        await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, GatewayMessages.Request("req-2"));
        await Poll.UntilAsync(() => worker.LogsWithId(513).Count == 1);
        execution.Complete(200, "null");
        await stopping.WaitAsync(Waits.Timeout);

        session.Received.Count(message => message.Kind == GatewayMessageType.WorkerRequestAck).ShouldBe(1);
        _engine.TryTake(out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Does_not_wait_for_an_execution_whose_lease_is_lost()
    {
        await using var worker = new WorkerHost(_gateway, engine: _engine);
        var session = await StartRequestAsync(worker);
        var execution = await _engine.NextAsync();
        var stopping = worker.StopAsync(TestContext.Current.CancellationToken);
        await session.NextAsync(GatewayMessageType.WorkerPause);

        await session.SendAsync(GatewayMessageType.WorkerRequestExtendLeaseAck, new WorkerRequestExtendLeaseAckData { RequestId = "req-1" });

        await stopping.WaitAsync(Waits.Timeout);
        execution.CancellationToken.IsCancellationRequested.ShouldBeTrue();
        session.CloseDescription.ShouldBe("WORKER_SHUTDOWN");
    }

    [Fact]
    public async Task Cancels_running_executions_when_the_host_stops_waiting()
    {
        await using var worker = new WorkerHost(_gateway, engine: _engine);
        var session = await StartRequestAsync(worker);
        var execution = await _engine.NextAsync();
        using var hostTimeout = new CancellationTokenSource();

        var stopping = worker.StopAsync(hostTimeout.Token);
        await session.NextAsync(GatewayMessageType.WorkerPause);
        await hostTimeout.CancelAsync();

        await stopping.WaitAsync(Waits.Timeout);
        await Poll.UntilAsync(() => execution.CancellationToken.IsCancellationRequested);
        await session.Closed.WaitAsync(Waits.Timeout);
        await Poll.UntilAsync(() => worker.LogsWithId(517).Count > 0);
        worker.LogsWithId(517).ShouldHaveSingleItem();
        session.Received.ShouldNotContain(message => message.Kind == GatewayMessageType.WorkerReply);
    }

    [Fact]
    public async Task Reconnects_during_shutdown_to_keep_the_lease_and_pauses_the_new_connection()
    {
        await using var worker = new WorkerHost(_gateway, engine: _engine);
        var first = await StartRequestAsync(worker);
        var execution = await _engine.NextAsync();
        var stopping = worker.StopAsync(TestContext.Current.CancellationToken);
        await first.NextAsync(GatewayMessageType.WorkerPause);

        first.Abort();

        var second = await _gateway.NextSessionAsync();
        await second.NextAsync(GatewayMessageType.WorkerPause);
        second.Received.ShouldNotContain(message => message.Kind == GatewayMessageType.WorkerReady);
        execution.Complete(200, "\"done\"");
        (await second.NextAsync(GatewayMessageType.WorkerReply, SDKResponse.Parser)).RequestId.ShouldBe("req-1");
        await stopping.WaitAsync(Waits.Timeout);
        second.CloseDescription.ShouldBe("WORKER_SHUTDOWN");
    }

    [Fact]
    public async Task Flushes_replies_the_gateway_did_not_acknowledge_before_the_close()
    {
        _gateway.AcknowledgeReplies = false;
        await using var worker = new WorkerHost(_gateway, engine: _engine);
        var session = await StartRequestAsync(worker);
        var execution = await _engine.NextAsync();
        var stopping = worker.StopAsync(TestContext.Current.CancellationToken);
        await session.NextAsync(GatewayMessageType.WorkerPause);

        execution.Complete(200, "\"done\"");

        await stopping.WaitAsync(Waits.Timeout);
        session.CloseDescription.ShouldBe("WORKER_SHUTDOWN");
        _gateway.TryTakeFlush(out var flush).ShouldBeTrue();
        flush.Reply.RequestId.ShouldBe("req-1");
    }

    [Fact]
    public async Task Stopping_before_the_first_connection_needs_no_gateway()
    {
        _gateway.StartStatus = _ => System.Net.HttpStatusCode.ServiceUnavailable;
        await using var worker = new WorkerHost(_gateway, engine: _engine);
        await worker.StartAsync();
        await Poll.UntilAsync(() => worker.LogsWithId(501).Count == 1);

        await worker.StopAsync(TestContext.Current.CancellationToken).WaitAsync(Waits.Timeout);

        _gateway.Sessions.ShouldBeEmpty();
    }

    private async Task<GatewaySession> StartRequestAsync(WorkerHost worker)
    {
        await worker.StartAsync();
        var session = await _gateway.NextReadySessionAsync();
        await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, GatewayMessages.Request("req-1"));
        await session.NextAsync(GatewayMessageType.WorkerRequestAck);
        return session;
    }
}
