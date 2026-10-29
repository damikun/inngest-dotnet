using Inngest.Connect.Protocol;
using Inngest.Tests.Connect.Support;

namespace Inngest.Tests.Connect;

// An execution outlives the connection it arrived on: its lease keeps being extended, and its reply goes out, on the
// connection that replaced it.
public sealed class ReconnectLeaseTests : IAsyncLifetime
{
    private readonly ControlledEngine _engine = new();
    private FakeGateway _gateway = null!;

    public async ValueTask InitializeAsync()
    {
        _gateway = await FakeGateway.StartAsync();

        // A lease interval passing at once would send several heartbeats in a row, which the gateway could not answer.
        _gateway.HeartbeatInterval = "1h";
    }

    public async ValueTask DisposeAsync() => await _gateway.DisposeAsync();

    [Fact]
    public async Task Lease_extensions_and_the_reply_move_to_the_connection_that_replaces_a_dropped_one()
    {
        await using var worker = new WorkerHost(_gateway, engine: _engine);
        await worker.StartAsync();
        var first = await _gateway.NextReadySessionAsync();
        await first.SendAsync(GatewayMessageType.GatewayExecutorRequest, GatewayMessages.Request("req-1", leaseId: "lease-1"));
        await first.NextAsync(GatewayMessageType.WorkerRequestAck);
        var execution = await _engine.NextAsync();

        first.Abort();
        var second = await _gateway.NextReadySessionAsync();
        worker.Time.Advance(TimeSpan.FromSeconds(30));

        (await second.NextAsync(GatewayMessageType.WorkerRequestExtendLease, WorkerRequestExtendLeaseData.Parser)).LeaseId.ShouldBe("lease-1");
        execution.Complete(200, "\"done\"");
        (await second.NextAsync(GatewayMessageType.WorkerReply, SDKResponse.Parser)).RequestId.ShouldBe("req-1");
        execution.CancellationToken.IsCancellationRequested.ShouldBeFalse();
    }
}
