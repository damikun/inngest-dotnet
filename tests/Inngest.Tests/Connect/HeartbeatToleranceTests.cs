using Inngest.Connect.Protocol;
using Inngest.Tests.Connect.Support;

namespace Inngest.Tests.Connect;

// How many heartbeats the gateway may leave unanswered before the worker gives up on the connection: two in a row,
// as in the TypeScript SDK, and an answer starts the count again.
public sealed class HeartbeatToleranceTests : IAsyncLifetime
{
    private FakeGateway _gateway = null!;

    public async ValueTask InitializeAsync()
    {
        _gateway = await FakeGateway.StartAsync();
        _gateway.HeartbeatInterval = "5s";
    }

    public async ValueTask DisposeAsync() => await _gateway.DisposeAsync();

    [Fact]
    public async Task One_unanswered_heartbeat_does_not_cost_the_connection()
    {
        _gateway.AnswerHeartbeats = false;
        await using var worker = new WorkerHost(_gateway);
        await worker.StartAsync();
        var session = await _gateway.NextReadySessionAsync();

        worker.Time.Advance(TimeSpan.FromSeconds(5));
        await session.NextAsync(GatewayMessageType.WorkerHeartbeat);

        _gateway.AnswerHeartbeats = true;
        for (var i = 0; i < 3; i++)
        {
            worker.Time.Advance(TimeSpan.FromSeconds(5));
            await session.NextAsync(GatewayMessageType.WorkerHeartbeat);
            await session.WaitForWorkerAsync();
        }

        _gateway.Starts.Count.ShouldBe(1);
        session.Closed.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task An_answer_starts_the_count_again_so_misses_that_are_not_in_a_row_are_forgiven()
    {
        await using var worker = new WorkerHost(_gateway);
        await worker.StartAsync();
        var session = await _gateway.NextReadySessionAsync();

        for (var i = 0; i < 6; i++)
        {
            _gateway.AnswerHeartbeats = i % 2 == 1;
            worker.Time.Advance(TimeSpan.FromSeconds(5));
            await session.NextAsync(GatewayMessageType.WorkerHeartbeat);
            if (_gateway.AnswerHeartbeats)
            {
                await session.WaitForWorkerAsync();
            }
        }

        _gateway.Starts.Count.ShouldBe(1);
        session.Closed.IsCompleted.ShouldBeFalse();
        worker.LogsWithId(508).ShouldBeEmpty();
    }

    [Fact]
    public async Task Heartbeats_go_to_the_connection_that_replaced_a_draining_one()
    {
        await using var worker = new WorkerHost(_gateway);
        await worker.StartAsync();
        var draining = await _gateway.NextReadySessionAsync();
        await draining.SendAsync(GatewayMessageType.GatewayClosing);
        var replacement = await _gateway.NextReadySessionAsync();
        await draining.Closed.WaitAsync(Waits.Timeout);

        worker.Time.Advance(TimeSpan.FromSeconds(5));

        await replacement.NextAsync(GatewayMessageType.WorkerHeartbeat);
        (await draining.TryNextAsync(GatewayMessageType.WorkerHeartbeat, TimeSpan.FromMilliseconds(200))).ShouldBeNull();
    }
}
