using Inngest.Connect.Protocol;
using Inngest.Tests.Connect.Support;

namespace Inngest.Tests.Connect;

// While an execution runs, the worker extends its lease on the gateway's interval, naming the latest lease id.
public sealed class LeaseTests : IAsyncLifetime
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NoMessage = TimeSpan.FromMilliseconds(200);

    private readonly ControlledEngine _engine = new();
    private FakeGateway _gateway = null!;

    public async ValueTask InitializeAsync()
    {
        _gateway = await FakeGateway.StartAsync();

        // Moving the clock by a whole lease interval at once would also send several heartbeats in a row, faster than
        // the gateway could answer them, and the worker would take that for a dead connection.
        _gateway.HeartbeatInterval = "1h";
    }

    public async ValueTask DisposeAsync() => await _gateway.DisposeAsync();

    [Fact]
    public async Task Extends_the_lease_once_an_interval_has_passed_since_the_acknowledgement()
    {
        await using var worker = new WorkerHost(_gateway, engine: _engine);
        var session = await StartRequestAsync(worker, "lease-1");

        worker.Time.Advance(Interval - TimeSpan.FromSeconds(1));
        (await session.TryNextAsync(GatewayMessageType.WorkerRequestExtendLease, NoMessage)).ShouldBeNull();
        worker.Time.Advance(TimeSpan.FromSeconds(1));

        var extension = await session.NextAsync(GatewayMessageType.WorkerRequestExtendLease, WorkerRequestExtendLeaseData.Parser);
        var request = GatewayMessages.Request("req-1");
        extension.ShouldBe(new WorkerRequestExtendLeaseData
        {
            RequestId = "req-1",
            AccountId = request.AccountId,
            EnvId = request.EnvId,
            AppId = request.AppId,
            FunctionSlug = request.FunctionSlug,
            StepId = "step",
            SystemTraceCtx = request.SystemTraceCtx,
            UserTraceCtx = request.UserTraceCtx,
            RunId = request.RunId,
            LeaseId = "lease-1",
        });
    }

    [Fact]
    public async Task Names_the_lease_id_of_the_latest_extension()
    {
        await using var worker = new WorkerHost(_gateway, engine: _engine);
        var session = await StartRequestAsync(worker, "lease-1");

        worker.Time.Advance(Interval);
        (await NextExtensionAsync(session, "req-1")).LeaseId.ShouldBe("lease-1");
        await session.SendAsync(GatewayMessageType.WorkerRequestExtendLeaseAck, new WorkerRequestExtendLeaseAckData { RequestId = "req-1", NewLeaseId = "lease-2" });
        await session.WaitForWorkerAsync();

        worker.Time.Advance(Interval);
        (await NextExtensionAsync(session, "req-1")).LeaseId.ShouldBe("lease-2");
        await session.SendAsync(GatewayMessageType.WorkerRequestExtendLeaseAck, new WorkerRequestExtendLeaseAckData { RequestId = "req-1", NewLeaseId = "lease-3" });
        await session.WaitForWorkerAsync();

        worker.Time.Advance(Interval);
        (await NextExtensionAsync(session, "req-1")).LeaseId.ShouldBe("lease-3");
    }

    [Fact]
    public async Task Stops_extending_once_the_gateway_does_not_renew_the_lease()
    {
        await using var worker = new WorkerHost(_gateway, engine: _engine);
        var session = await StartRequestAsync(worker, "lease-1");
        worker.Time.Advance(Interval);
        await session.NextAsync(GatewayMessageType.WorkerRequestExtendLease);

        // An acknowledgement without a new lease id: the lease expired or another worker holds it now.
        await session.SendAsync(GatewayMessageType.WorkerRequestExtendLeaseAck, new WorkerRequestExtendLeaseAckData { RequestId = "req-1" });
        await session.WaitForWorkerAsync();
        worker.Time.Advance(Interval);

        (await TryNextExtensionAsync(session, "req-1")).ShouldBeNull();
        worker.LogsWithId(518).ShouldHaveSingleItem().Message.ShouldContain("req-1");
    }

    [Fact]
    public async Task Stops_extending_once_the_execution_is_done()
    {
        await using var worker = new WorkerHost(_gateway, engine: _engine);
        var session = await StartRequestAsync(worker, "lease-1");

        (await _engine.NextAsync()).Complete(200, "null");
        await session.NextAsync(GatewayMessageType.WorkerReply);
        worker.Time.Advance(Interval);

        (await session.TryNextAsync(GatewayMessageType.WorkerRequestExtendLease, NoMessage)).ShouldBeNull();
    }

    [Fact]
    public async Task Ignores_answers_for_requests_that_are_done()
    {
        await using var worker = new WorkerHost(_gateway, engine: _engine);
        var session = await StartRequestAsync(worker, "lease-1");
        (await _engine.NextAsync()).Complete(200, "null");
        await session.NextAsync(GatewayMessageType.WorkerReply);

        await session.SendAsync(GatewayMessageType.WorkerRequestExtendLeaseAck, new WorkerRequestExtendLeaseAckData { RequestId = "req-1" });
        await session.WaitForWorkerAsync();

        worker.LogsWithId(518).ShouldBeEmpty();
    }

    [Fact]
    public async Task Extends_each_running_request_on_its_own()
    {
        await using var worker = new WorkerHost(_gateway, engine: _engine);
        var session = await StartRequestAsync(worker, "lease-a");
        await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, GatewayMessages.Request("req-2", leaseId: "lease-b"));
        await session.NextAsync(GatewayMessageType.WorkerRequestAck);

        worker.Time.Advance(Interval);

        var first = await session.NextAsync(GatewayMessageType.WorkerRequestExtendLease, WorkerRequestExtendLeaseData.Parser);
        var second = await session.NextAsync(GatewayMessageType.WorkerRequestExtendLease, WorkerRequestExtendLeaseData.Parser);
        new[] { (first.RequestId, first.LeaseId), (second.RequestId, second.LeaseId) }
            .ShouldBe([("req-1", "lease-a"), ("req-2", "lease-b")], ignoreOrder: true);
    }

    [Fact]
    public async Task A_request_sent_again_while_it_runs_is_acknowledged_and_takes_the_new_lease()
    {
        await using var worker = new WorkerHost(_gateway, engine: _engine);
        var session = await StartRequestAsync(worker, "lease-1");
        await _engine.NextAsync();

        await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, GatewayMessages.Request("req-1", leaseId: "lease-9"));

        (await session.NextAsync(GatewayMessageType.WorkerRequestAck, WorkerRequestAckData.Parser)).RequestId.ShouldBe("req-1");
        _engine.TryTake(out _).ShouldBeFalse();
        worker.Time.Advance(Interval);
        (await session.NextAsync(GatewayMessageType.WorkerRequestExtendLease, WorkerRequestExtendLeaseData.Parser)).LeaseId.ShouldBe("lease-9");
    }

    [Fact]
    public async Task A_late_refusal_of_an_older_lease_does_not_stop_a_request_that_was_leased_again()
    {
        await using var worker = new WorkerHost(_gateway, engine: _engine);
        var session = await StartRequestAsync(worker, "lease-1");
        await _engine.NextAsync();
        worker.Time.Advance(Interval);
        (await NextExtensionAsync(session, "req-1")).LeaseId.ShouldBe("lease-1");

        // Before the gateway answers that extension it sends the running request again with a new lease, so the answer
        // that follows refuses lease-1, which is no longer the current lease.
        await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, GatewayMessages.Request("req-1", leaseId: "lease-9"));
        await session.NextAsync(GatewayMessageType.WorkerRequestAck);
        await session.SendAsync(GatewayMessageType.WorkerRequestExtendLeaseAck, new WorkerRequestExtendLeaseAckData { RequestId = "req-1" });
        await session.WaitForWorkerAsync();
        worker.Time.Advance(Interval);

        (await NextExtensionAsync(session, "req-1")).LeaseId.ShouldBe("lease-9");
        worker.LogsWithId(518).ShouldBeEmpty();
        worker.LogsWithId(535).ShouldHaveSingleItem().Message.ShouldContain("req-1");
    }

    // The probes of WaitForWorkerAsync are requests too: when the clock moves while one still runs, it extends its own
    // lease. Tests that move the clock after a probe therefore look at the extensions of their own request only.
    private static async Task<WorkerRequestExtendLeaseData> NextExtensionAsync(GatewaySession session, string requestId)
    {
        while (true)
        {
            var extension = await session.NextAsync(GatewayMessageType.WorkerRequestExtendLease, WorkerRequestExtendLeaseData.Parser);
            if (extension.RequestId == requestId)
            {
                return extension;
            }
        }
    }

    private static async Task<WorkerRequestExtendLeaseData?> TryNextExtensionAsync(GatewaySession session, string requestId)
    {
        while (await session.TryNextAsync(GatewayMessageType.WorkerRequestExtendLease, NoMessage) is { } message)
        {
            var extension = WorkerRequestExtendLeaseData.Parser.ParseFrom(message.Payload);
            if (extension.RequestId == requestId)
            {
                return extension;
            }
        }

        return null;
    }

    private async Task<GatewaySession> StartRequestAsync(WorkerHost worker, string leaseId)
    {
        await worker.StartAsync();
        var session = await _gateway.NextReadySessionAsync();
        await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, GatewayMessages.Request("req-1", leaseId: leaseId));
        await session.NextAsync(GatewayMessageType.WorkerRequestAck);
        return session;
    }
}
