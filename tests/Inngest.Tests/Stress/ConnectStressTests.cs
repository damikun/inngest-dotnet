using System.Text.Json;
using Inngest.Connect.Protocol;
using Inngest.Execution;
using Inngest.Tests.Connect.Support;

namespace Inngest.Tests.Stress;

// The Connect worker with the real engine and many executions at once over one socket: every request is
// acknowledged once and answered once with its own result, also when the gateway sends a request twice or drains
// while executions run, and nothing an execution leaves behind outlives it.
public sealed class ConnectStressTests : IAsyncLifetime
{
    private const int Requests = 1000;

    private FakeGateway _gateway = null!;

    public async ValueTask InitializeAsync()
    {
        _gateway = await FakeGateway.StartAsync();
        _gateway.HeartbeatInterval = "1h";
    }

    public async ValueTask DisposeAsync() => await _gateway.DisposeAsync();

    [Fact]
    public async Task Answers_a_thousand_concurrent_requests_each_exactly_once()
    {
        await using var worker = new WorkerHost(_gateway, functions: static inngest => inngest.AddFunction(WorkerHost.Orders, PriceOrder));
        await worker.StartAsync();
        var session = await _gateway.NextReadySessionAsync();

        for (var order = 0; order < Requests; order++)
        {
            await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, Request(order));
        }

        var replies = await RepliesAsync([session], Requests);

        Acknowledged(session).ShouldBe([.. Enumerable.Range(0, Requests).Select(RequestId)], ignoreOrder: true);
        replies.Keys.ShouldBe([.. Enumerable.Range(0, Requests).Select(RequestId)], ignoreOrder: true);
        for (var order = 0; order < Requests; order++)
        {
            ShouldBeTheAnswerFor(order, replies[RequestId(order)]);
        }

        _gateway.TryTakeFlush(out _).ShouldBeFalse();

        // The worker stops at once only when it counts no execution as running any more.
        await worker.StopAsync(TestContext.Current.CancellationToken).WaitAsync(Waits.Timeout);
        session.CloseDescription.ShouldBe("WORKER_SHUTDOWN");
    }

    [Fact]
    public async Task Requests_sent_twice_while_they_run_execute_once()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;
        await using var worker = new WorkerHost(_gateway, functions: inngest => inngest.AddFunction(WorkerHost.Orders, async (context, _) =>
        {
            Interlocked.Increment(ref executions);
            await gate.Task;
            return new { order = context.Event.Data.GetProperty("orderId").GetInt32() };
        }));
        await worker.StartAsync();
        var session = await _gateway.NextReadySessionAsync();

        for (var order = 0; order < Requests; order++)
        {
            await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, Request(order));
            await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, Request(order, lease: "01JLEASE-RENEWED"));
        }

        await Poll.UntilAsync(() => Acknowledged(session).Count == 2 * Requests);
        gate.SetResult();
        var replies = await RepliesAsync([session], Requests);

        Volatile.Read(ref executions).ShouldBe(Requests);
        replies.Count.ShouldBe(Requests);
        session.Received.Count(static message => message.Kind == GatewayMessageType.WorkerReply).ShouldBe(Requests);
        replies[RequestId(7)].Body.ToStringUtf8().ShouldBe("""{"order":7}""");
    }

    [Fact]
    public async Task Executions_that_run_while_the_gateway_drains_reply_on_the_new_connection()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var worker = new WorkerHost(_gateway, functions: inngest => inngest.AddFunction(WorkerHost.Orders, async (context, cancellationToken) =>
        {
            await gate.Task;
            return await PriceOrder(context, cancellationToken);
        }));
        await worker.StartAsync();
        var draining = await _gateway.NextReadySessionAsync();
        for (var order = 0; order < Requests; order++)
        {
            await draining.SendAsync(GatewayMessageType.GatewayExecutorRequest, Request(order));
        }

        await Poll.UntilAsync(() => Acknowledged(draining).Count == Requests);
        await draining.SendAsync(GatewayMessageType.GatewayClosing);
        var replacement = await _gateway.NextReadySessionAsync();
        gate.SetResult();

        var replies = await RepliesAsync([replacement], Requests);

        replies.Count.ShouldBe(Requests);
        for (var order = 0; order < Requests; order++)
        {
            ShouldBeTheAnswerFor(order, replies[RequestId(order)]);
        }

        draining.Received.ShouldNotContain(message => message.Kind == GatewayMessageType.WorkerReply);
        _gateway.TryTakeFlush(out _).ShouldBeFalse();
    }

    // A function waiting on a step that never completes in its invocation must not be kept reachable by the
    // worker's token, which lives as long as the worker does.
    [Fact]
    public async Task Executions_abandoned_on_a_step_do_not_outlive_their_reply()
    {
        var buffers = new WeakReference[Requests];
        var tracked = 0;
        await using var worker = new WorkerHost(_gateway, functions: inngest => inngest.AddFunction(WorkerHost.Orders, async (context, cancellationToken) =>
        {
            var buffer = new byte[1024];
            buffers[Interlocked.Increment(ref tracked) - 1] = new WeakReference(buffer);
            using var registration = cancellationToken.Register(static state => Array.Clear((byte[])state!), buffer);
            await context.Step.SleepAsync("wait-a-day", TimeSpan.FromDays(1));
            return buffer.Length;
        }));
        await worker.StartAsync();
        var session = await _gateway.NextReadySessionAsync();

        for (var order = 0; order < Requests; order++)
        {
            await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, Request(order));
        }

        var replies = await RepliesAsync([session], Requests);
        replies.Values.ShouldAllBe(static reply => reply.Status == SDKResponseStatus.NotCompleted);

        // The last executions may still be finishing when their replies arrive, so the count gets a moment to settle.
        var alive = Requests;
        for (var attempt = 0; attempt < 50 && alive > 0; attempt++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            alive = buffers.Count(static buffer => buffer.IsAlive);
        }

        alive.ShouldBe(0);
    }

    private static async Task<object?> PriceOrder(FunctionContext<JsonElement> context, CancellationToken cancellationToken)
    {
        var order = context.Event.Data.GetProperty("orderId").GetInt32();
        var total = await context.Step.RunAsync("price", () => order * 3);
        return new { order, total };
    }

    // Even orders are first calls, which run the step; odd ones have its result and finish the run.
    private static void ShouldBeTheAnswerFor(int order, SDKResponse reply)
    {
        if (order % 2 == 0)
        {
            reply.Status.ShouldBe(SDKResponseStatus.NotCompleted);
            using var ops = JsonDocument.Parse(reply.Body.ToStringUtf8());
            var op = ops.RootElement.EnumerateArray().ShouldHaveSingleItem();
            op.GetProperty("op").GetString().ShouldBe("StepRun");
            op.GetProperty("data").GetInt32().ShouldBe(order * 3);
        }
        else
        {
            reply.Status.ShouldBe(SDKResponseStatus.Done);
            reply.Body.ToStringUtf8().ShouldBe($$"""{"order":{{order}},"total":{{order * 3}}}""");
        }
    }

    private static string RequestId(int order) => $"request-{order:D4}";

    private static GatewayExecutorRequestData Request(int order, string lease = "01JLEASE00000000000000000001")
    {
        var price = StepIds.Hash("price");
        var memoized = order % 2 == 1;
        var @event = $$$"""{"data":{"orderId":{{{order}}}},"id":"01JEVENT{{{order:D6}}}","name":"order/created","ts":1793268000000,"user":{}}""";
        var payload =
            $$"""{"ctx":{"attempt":0,"disable_immediate_execution":false,"fn_id":"function-1","max_attempts":4,"run_id":"01JRUN{{order:D6}}","stack":{"current":{{(memoized ? 1 : 0)}},"stack":[{{(memoized ? $"\"{price}\"" : "")}}]},"use_api":false},"event":{{@event}},"events":[{{@event}}],"steps":{{{(memoized ? $"\"{price}\":{{\"data\":{order * 3}}}" : "")}}},"version":2}""";
        var request = GatewayMessages.Request(RequestId(order), payload: payload, leaseId: lease);
        request.RunId = $"01JRUN{order:D6}";
        return request;
    }

    private static List<string> Acknowledged(GatewaySession session) =>
        [.. session.Received
            .Where(static message => message.Kind == GatewayMessageType.WorkerRequestAck)
            .Select(static message => WorkerRequestAckData.Parser.ParseFrom(message.Payload).RequestId)];

    // Waits for this many distinct replies over the sessions and returns the last reply to each request.
    private static async Task<Dictionary<string, SDKResponse>> RepliesAsync(IReadOnlyList<GatewaySession> sessions, int count)
    {
        Dictionary<string, SDKResponse> Collect() =>
            sessions
                .SelectMany(static session => session.Received)
                .Where(static message => message.Kind == GatewayMessageType.WorkerReply)
                .Select(static message => SDKResponse.Parser.ParseFrom(message.Payload))
                .GroupBy(static reply => reply.RequestId)
                .ToDictionary(static group => group.Key, static group => group.Last());

        await Poll.UntilAsync(() => Collect().Count >= count);
        return Collect();
    }
}
