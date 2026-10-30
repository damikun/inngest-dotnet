using System.Net;
using Inngest.Connect.Protocol;
using Inngest.Signing;
using Inngest.Tests.Connect.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Tests.Connect;

// A reply is kept until the gateway acknowledges it. One that is not acknowledged within five seconds, or that finds
// no connection, goes to Inngest over HTTP instead.
public sealed class ReplyTests : IAsyncLifetime
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    private FakeGateway _gateway = null!;

    public async ValueTask InitializeAsync()
    {
        _gateway = await FakeGateway.StartAsync();
        _gateway.HeartbeatInterval = "1h";
    }

    public async ValueTask DisposeAsync() => await _gateway.DisposeAsync();

    [Fact]
    public async Task An_acknowledged_reply_is_not_sent_again()
    {
        await using var worker = new WorkerHost(_gateway);
        var session = await ConnectAsync(worker);

        await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, GatewayMessages.Request("req-1"));
        await session.NextAsync(GatewayMessageType.WorkerReply);
        await session.WaitForWorkerAsync();
        worker.Time.Advance(Deadline);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // The probe's own reply may still be waiting for its acknowledgement, so only req-1 is checked.
        while (_gateway.TryTakeFlush(out var flush))
        {
            flush.Reply.RequestId.ShouldNotBe("req-1");
        }

        worker.LogsWithId(521).ShouldNotContain(record => record.Message.Contains("req-1"));
    }

    [Fact]
    public async Task A_reply_the_gateway_does_not_acknowledge_in_time_is_flushed_over_http()
    {
        _gateway.AcknowledgeReplies = false;
        await using var worker = new WorkerHost(_gateway);
        var session = await ConnectAsync(worker);
        var request = GatewayMessages.Request("req-1");

        await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, request);
        var sent = await session.NextAsync(GatewayMessageType.WorkerReply, SDKResponse.Parser);
        worker.Time.Advance(Deadline - TimeSpan.FromMilliseconds(1));
        await Task.Delay(200, TestContext.Current.CancellationToken);
        _gateway.TryTakeFlush(out _).ShouldBeFalse();
        worker.Time.Advance(TimeSpan.FromMilliseconds(1));

        var flush = await _gateway.NextFlushAsync();
        flush.ContentType.ShouldBe("application/protobuf");
        flush.Authorization.ShouldBeEmpty();
        flush.Reply.ShouldBe(sent);
        flush.Reply.SystemTraceCtx.ShouldBe(request.SystemTraceCtx);
        worker.LogsWithId(521).ShouldHaveSingleItem().Message.ShouldContain("req-1");
    }

    [Fact]
    public async Task Flushes_with_the_signing_key_in_cloud_mode()
    {
        const string Key = "signkey-prod-00112233445566778899aabbccddeeff";
        _gateway.AcknowledgeReplies = false;
        await using var worker = new WorkerHost(_gateway, inngest: options =>
        {
            options.IsDev = false;
            options.SigningKey = Key;
            options.ApiBaseUrl = _gateway.BaseUrl;
        });
        var session = await ConnectAsync(worker);

        await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, GatewayMessages.Request("req-1"));
        await session.NextAsync(GatewayMessageType.WorkerReply);
        worker.Time.Advance(Deadline);

        (await _gateway.NextFlushAsync()).Authorization.ShouldBe($"Bearer {SigningKey.Create(Key)!.BearerToken}");
    }

    [Fact]
    public async Task Tries_a_failed_flush_again_after_a_backoff()
    {
        var attempts = 0;
        _gateway.AcknowledgeReplies = false;
        _gateway.FlushStatus = _ => Interlocked.Increment(ref attempts) == 1 ? HttpStatusCode.ServiceUnavailable : null;
        await using var worker = new WorkerHost(_gateway);
        var session = await ConnectAsync(worker);
        await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, GatewayMessages.Request("req-1"));
        await session.NextAsync(GatewayMessageType.WorkerReply);

        var backoff = worker.Time.NextTimerAsync(TimeSpan.FromSeconds(1));
        worker.Time.Advance(Deadline);
        (await _gateway.NextFlushAsync()).Status.ShouldBe(HttpStatusCode.ServiceUnavailable);
        await backoff;
        worker.Time.Advance(TimeSpan.FromSeconds(1));

        var retried = await _gateway.NextFlushAsync();
        retried.Status.ShouldBe(HttpStatusCode.OK);
        retried.Reply.RequestId.ShouldBe("req-1");
        worker.LogsWithId(523).ShouldHaveSingleItem().Message.ShouldContain("503");
    }

    [Fact]
    public async Task A_reply_that_finds_no_connection_goes_over_http_at_once()
    {
        var engine = new ControlledEngine();
        await using var worker = new WorkerHost(_gateway, engine: engine);
        var session = await ConnectAsync(worker);
        await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, GatewayMessages.Request("req-1"));
        var execution = await engine.NextAsync();

        // The connection drops, and Inngest cannot be reached for a new one.
        _gateway.StartStatus = _ => HttpStatusCode.ServiceUnavailable;
        session.Abort();
        await Poll.UntilAsync(() => worker.LogsWithId(506).Count == 1);
        execution.Complete(200, "\"done\"");

        var flush = await _gateway.NextFlushAsync();
        flush.Reply.RequestId.ShouldBe("req-1");
        flush.Reply.Body.ToStringUtf8().ShouldBe("\"done\"");
        worker.LogsWithId(520).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_flush_that_a_handler_fails_with_its_own_exception_is_tried_again()
    {
        _gateway.AcknowledgeReplies = false;
        await using var worker = new WorkerHost(_gateway, configure: services => services
            .AddHttpClient(InngestServiceCollectionExtensions.HttpClientName)
            .AddHttpMessageHandler(() => new FirstFlushFails()));
        var session = await ConnectAsync(worker);
        await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, GatewayMessages.Request("req-1"));
        await session.NextAsync(GatewayMessageType.WorkerReply);

        var backoff = worker.Time.NextTimerAsync(TimeSpan.FromSeconds(1));
        worker.Time.Advance(Deadline);
        await backoff;
        worker.Time.Advance(TimeSpan.FromSeconds(1));

        (await _gateway.NextFlushAsync()).Reply.RequestId.ShouldBe("req-1");
        worker.LogsWithId(523).ShouldHaveSingleItem().Message.ShouldContain("circuit is open");
    }

    [Fact]
    public async Task A_reply_Inngest_refuses_is_dropped_so_the_next_one_still_goes_out()
    {
        _gateway.AcknowledgeReplies = false;
        _gateway.FlushStatus = call => call.Reply.RequestId == "req-1" ? HttpStatusCode.BadRequest : null;
        await using var worker = new WorkerHost(_gateway);
        var session = await ConnectAsync(worker);
        await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, GatewayMessages.Request("req-1"));
        await session.NextAsync(GatewayMessageType.WorkerReply);
        await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, GatewayMessages.Request("req-2"));
        await session.NextAsync(GatewayMessageType.WorkerReply);

        worker.Time.Advance(Deadline);

        (await _gateway.NextFlushAsync()).Status.ShouldBe(HttpStatusCode.BadRequest);
        var next = await _gateway.NextFlushAsync();
        next.Reply.RequestId.ShouldBe("req-2");
        next.Status.ShouldBe(HttpStatusCode.OK);
        worker.LogsWithId(534).ShouldHaveSingleItem().Message.ShouldContain("req-1");
    }

    private async Task<GatewaySession> ConnectAsync(WorkerHost worker)
    {
        await worker.StartAsync();
        return await _gateway.NextReadySessionAsync();
    }

    // Stands in for a resilience handler: its timeouts and open circuits are not HttpRequestExceptions.
    private sealed class FirstFlushFails : DelegatingHandler
    {
        private int _flushes;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri!.AbsolutePath.EndsWith("/flush", StringComparison.Ordinal) && Interlocked.Increment(ref _flushes) == 1
                ? throw new CircuitOpenException()
                : base.SendAsync(request, cancellationToken);
    }

    private sealed class CircuitOpenException() : Exception("The circuit is open.");
}
