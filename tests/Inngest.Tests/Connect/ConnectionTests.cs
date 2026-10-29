using System.Net;
using System.Net.WebSockets;
using Inngest.Connect.Protocol;
using Inngest.Signing;
using Inngest.Tests.Connect.Support;

namespace Inngest.Tests.Connect;

// The worker keeps one connection up: it replaces a connection whose gateway drains before closing it, reconnects
// when a socket drops or stops answering, and backs off while Inngest cannot be reached.
public sealed class ConnectionTests : IAsyncLifetime
{
    private FakeGateway _gateway = null!;

    public async ValueTask InitializeAsync() => _gateway = await FakeGateway.StartAsync();

    public async ValueTask DisposeAsync() => await _gateway.DisposeAsync();

    [Fact]
    public async Task Moves_to_a_new_connection_when_the_gateway_drains_and_then_closes_the_old_one()
    {
        _gateway.HeartbeatInterval = "1h";
        var engine = new ControlledEngine();
        await using var worker = new WorkerHost(_gateway, engine: engine);
        await worker.StartAsync();
        var draining = await _gateway.NextReadySessionAsync();
        await draining.SendAsync(GatewayMessageType.GatewayExecutorRequest, GatewayMessages.Request("req-1", leaseId: "lease-1"));
        await draining.NextAsync(GatewayMessageType.WorkerRequestAck);
        var execution = await engine.NextAsync();

        await draining.SendAsync(GatewayMessageType.GatewayClosing);

        var replacement = await _gateway.NextReadySessionAsync();
        replacement.Hello.ConnectionId.ShouldNotBe(draining.Hello.ConnectionId);
        _gateway.Starts.Count.ShouldBe(2);
        await draining.Closed.WaitAsync(Waits.Timeout);
        draining.CloseStatus.ShouldBe(WebSocketCloseStatus.NormalClosure);
        draining.CloseDescription.ShouldBe("GATEWAY_DRAINING");
        worker.LogsWithId(507).ShouldHaveSingleItem();

        // The request that came in on the old connection keeps its lease and replies on the new one.
        worker.Time.Advance(TimeSpan.FromSeconds(30));
        (await replacement.NextAsync(GatewayMessageType.WorkerRequestExtendLease, WorkerRequestExtendLeaseData.Parser)).LeaseId.ShouldBe("lease-1");
        execution.Complete(200, "\"done\"");
        (await replacement.NextAsync(GatewayMessageType.WorkerReply, SDKResponse.Parser)).RequestId.ShouldBe("req-1");
    }

    [Fact]
    public async Task Reconnects_at_once_when_the_socket_drops()
    {
        await using var worker = new WorkerHost(_gateway);
        await worker.StartAsync();
        var first = await _gateway.NextReadySessionAsync();

        first.Abort();

        var second = await _gateway.NextReadySessionAsync();
        second.Hello.AuthData.SessionToken.ShouldBe("session-2");

        // As in the TypeScript SDK, Inngest is asked for another gateway than the one that failed.
        _gateway.Starts[1].Request.ExcludeGateways.ShouldBe([FakeGateway.GatewayGroup]);
        worker.LogsWithId(506).ShouldHaveSingleItem().Message.ShouldContain(first.Hello.ConnectionId);
    }

    [Fact]
    public async Task Reconnects_when_the_gateway_closes_the_connection()
    {
        await using var worker = new WorkerHost(_gateway);
        await worker.StartAsync();
        var first = await _gateway.NextReadySessionAsync();

        await first.CloseAsync(WebSocketCloseStatus.InternalServerError, "connect_internal_error");

        await _gateway.NextReadySessionAsync();
        worker.LogsWithId(506).ShouldHaveSingleItem().Message.ShouldContain("1011 connect_internal_error");
    }

    [Fact]
    public async Task Backs_off_while_inngest_cannot_be_reached()
    {
        var refusals = 2;
        _gateway.StartStatus = _ => Interlocked.Decrement(ref refusals) >= 0 ? HttpStatusCode.ServiceUnavailable : null;
        await using var worker = new WorkerHost(_gateway);

        var firstWait = worker.Time.NextTimerAsync(TimeSpan.FromSeconds(1));
        await worker.StartAsync();
        await firstWait;
        worker.Time.Advance(TimeSpan.FromMilliseconds(999));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        _gateway.Starts.Count.ShouldBe(1);

        var secondWait = worker.Time.NextTimerAsync(TimeSpan.FromSeconds(2));
        worker.Time.Advance(TimeSpan.FromMilliseconds(1));
        await secondWait;
        _gateway.Starts.Count.ShouldBe(2);
        worker.Time.Advance(TimeSpan.FromSeconds(2));

        await _gateway.NextReadySessionAsync();
        _gateway.Starts.Count.ShouldBe(3);
        worker.LogsWithId(501).Select(record => record.Message).ShouldAllBe(message => message.Contains("503"));
    }

    [Fact]
    public async Task Waits_only_briefly_when_a_draining_gateway_turns_the_connection_away()
    {
        var turnedAway = 0;
        _gateway.OnWorkerConnect = async session =>
        {
            if (Interlocked.Increment(ref turnedAway) > 1)
            {
                return true;
            }

            await session.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "connect_gateway_closing");
            return false;
        };
        await using var worker = new WorkerHost(_gateway);

        var wait = worker.Time.NextTimerAsync(due => due >= TimeSpan.FromMilliseconds(500) && due <= TimeSpan.FromMilliseconds(1500), "0.5 to 1.5 s");
        await worker.StartAsync();
        worker.Time.Advance(await wait);

        await _gateway.NextReadySessionAsync();
    }

    [Fact]
    public async Task Switches_to_the_fallback_key_when_inngest_rejects_the_signing_key()
    {
        var current = SigningKey.Create("signkey-prod-aaaaaaaaaaaaaaaa")!;
        var fallback = SigningKey.Create("signkey-prod-bbbbbbbbbbbbbbbb")!;
        _gateway.StartStatus = call => call.Authorization == $"Bearer {current.BearerToken}" ? HttpStatusCode.Unauthorized : null;
        await using var worker = new WorkerHost(_gateway, inngest: options =>
        {
            options.IsDev = false;
            options.SigningKey = current.Value;
            options.SigningKeyFallback = fallback.Value;
            options.ApiBaseUrl = _gateway.BaseUrl;
        });

        var backoff = worker.Time.NextTimerAsync(TimeSpan.FromSeconds(1));
        await worker.StartAsync();
        await backoff;
        worker.Time.Advance(TimeSpan.FromSeconds(1));

        await _gateway.NextReadySessionAsync();
        _gateway.Starts.Select(start => start.Authorization).ShouldBe([$"Bearer {current.BearerToken}", $"Bearer {fallback.BearerToken}"]);
        worker.LogsWithId(503).ShouldHaveSingleItem().Message.ShouldContain("fallback");
    }

    [Fact]
    public async Task Switches_back_to_the_signing_key_when_the_fallback_is_rejected_too()
    {
        var current = SigningKey.Create("signkey-prod-aaaaaaaaaaaaaaaa")!;
        var fallback = SigningKey.Create("signkey-prod-bbbbbbbbbbbbbbbb")!;
        var rejections = 2;
        _gateway.StartStatus = _ => Interlocked.Decrement(ref rejections) >= 0 ? HttpStatusCode.Unauthorized : null;
        await using var worker = new WorkerHost(_gateway, inngest: options =>
        {
            options.IsDev = false;
            options.SigningKey = current.Value;
            options.SigningKeyFallback = fallback.Value;
            options.ApiBaseUrl = _gateway.BaseUrl;
        });

        await worker.StartAsync();
        await worker.AdvanceUntilAsync(() => _gateway.Starts.Count == 3, TimeSpan.FromMilliseconds(500));

        await _gateway.NextReadySessionAsync();
        _gateway.Starts.Select(start => start.Authorization)
            .ShouldBe([$"Bearer {current.BearerToken}", $"Bearer {fallback.BearerToken}", $"Bearer {current.BearerToken}"]);
    }

    [Fact]
    public async Task Sends_heartbeats_on_the_interval_the_gateway_gave()
    {
        _gateway.HeartbeatInterval = "5s";
        await using var worker = new WorkerHost(_gateway);
        await worker.StartAsync();
        var session = await _gateway.NextReadySessionAsync();

        worker.Time.Advance(TimeSpan.FromSeconds(4));
        (await session.TryNextAsync(GatewayMessageType.WorkerHeartbeat, TimeSpan.FromMilliseconds(200))).ShouldBeNull();

        for (var i = 0; i < 5; i++)
        {
            worker.Time.Advance(TimeSpan.FromSeconds(i == 0 ? 1 : 5));
            await session.NextAsync(GatewayMessageType.WorkerHeartbeat);
            await session.WaitForWorkerAsync();
        }

        // Every heartbeat was answered, so the connection stays.
        _gateway.Starts.Count.ShouldBe(1);
        session.Closed.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task Reconnects_when_the_gateway_stops_answering_heartbeats()
    {
        _gateway.AnswerHeartbeats = false;
        await using var worker = new WorkerHost(_gateway);
        await worker.StartAsync();
        var silent = await _gateway.NextReadySessionAsync();

        for (var i = 0; i < 2; i++)
        {
            worker.Time.Advance(TimeSpan.FromSeconds(10));
            await silent.NextAsync(GatewayMessageType.WorkerHeartbeat);
        }

        _gateway.AnswerHeartbeats = true;
        worker.Time.Advance(TimeSpan.FromSeconds(10));

        await _gateway.NextReadySessionAsync();
        await silent.Closed.WaitAsync(Waits.Timeout);
        worker.LogsWithId(508).ShouldHaveSingleItem().Message.ShouldContain(silent.Hello.ConnectionId);
    }
}
