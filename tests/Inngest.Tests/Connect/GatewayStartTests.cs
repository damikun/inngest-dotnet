using System.Net;
using Inngest.Tests.Connect.Support;

namespace Inngest.Tests.Connect;

// What the worker does when the first steps of connecting go wrong: Inngest refuses to hand out a gateway, or the
// gateway takes the socket and says nothing.
public sealed class GatewayStartTests : IAsyncLifetime
{
    private FakeGateway _gateway = null!;

    public async ValueTask InitializeAsync() => _gateway = await FakeGateway.StartAsync();

    public async ValueTask DisposeAsync() => await _gateway.DisposeAsync();

    [Fact]
    public async Task A_refusal_for_too_many_connections_is_reported_and_retried()
    {
        var attempts = 0;
        _gateway.StartStatus = _ => Interlocked.Increment(ref attempts) == 1 ? HttpStatusCode.TooManyRequests : null;
        await using var worker = new WorkerHost(_gateway);

        await worker.StartAsync();
        await worker.AdvanceUntilAsync(() => _gateway.Starts.Count == 2, TimeSpan.FromMilliseconds(500));
        await _gateway.NextReadySessionAsync();

        worker.LogsWithId(504).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_gateway_that_never_says_hello_is_given_up_on_after_ten_seconds_and_another_is_asked_for()
    {
        var sessions = 0;
        _gateway.WithholdHello = () => Interlocked.Increment(ref sessions) == 1;
        await using var worker = new WorkerHost(_gateway);

        await worker.StartAsync();
        await worker.AdvanceUntilAsync(() => _gateway.Starts.Count == 2, TimeSpan.FromSeconds(1));
        await _gateway.NextReadySessionAsync();

        worker.LogsWithId(501).ShouldHaveSingleItem().Message.ShouldContain("did not answer within 10 seconds");
        _gateway.Starts[1].Request.ExcludeGateways.ShouldBe([FakeGateway.GatewayGroup]);
    }
}
