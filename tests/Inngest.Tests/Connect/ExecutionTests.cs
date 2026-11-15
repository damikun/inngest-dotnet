using System.Text.Json;
using Inngest.Connect.Protocol;
using Inngest.Internal;
using Inngest.Tests.Connect.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Tests.Connect;

// An executor request is acknowledged at once, run through the execution engine like an HTTP call, and answered with
// an SDKResponse holding what the HTTP response would have held.
public sealed class ExecutionTests : IAsyncLifetime
{
    private FakeGateway _gateway = null!;

    public async ValueTask InitializeAsync() => _gateway = await FakeGateway.StartAsync();

    public async ValueTask DisposeAsync() => await _gateway.DisposeAsync();

    [Fact]
    public async Task Acknowledges_a_request_runs_the_function_and_replies_with_its_output()
    {
        await using var worker = new WorkerHost(_gateway);
        await worker.StartAsync();
        var session = await _gateway.NextReadySessionAsync();
        var request = GatewayMessages.Request("req-1");

        await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, request);

        var ack = await session.NextAsync(GatewayMessageType.WorkerRequestAck, WorkerRequestAckData.Parser);
        ack.ShouldBe(new WorkerRequestAckData
        {
            RequestId = "req-1",
            AccountId = "account-1",
            EnvId = "env-1",
            AppId = "app-1",
            FunctionSlug = "shop-orders",
            StepId = "step",
            SystemTraceCtx = request.SystemTraceCtx,
            UserTraceCtx = request.UserTraceCtx,
            RunId = request.RunId,
        });

        var reply = await session.NextAsync(GatewayMessageType.WorkerReply, SDKResponse.Parser);
        reply.Status.ShouldBe(SDKResponseStatus.Done);
        reply.Body.ToStringUtf8().ShouldBe("""{"received":42}""");
        reply.RequestId.ShouldBe("req-1");
        reply.AccountId.ShouldBe("account-1");
        reply.EnvId.ShouldBe("env-1");
        reply.AppId.ShouldBe("app-1");
        reply.RunId.ShouldBe(request.RunId);
        reply.NoRetry.ShouldBeFalse();
        reply.HasRetryAfter.ShouldBeFalse();
        reply.SdkVersion.ShouldBe(SdkInfo.Header);
        reply.RequestVersion.ShouldBe(2u);
        reply.SystemTraceCtx.ShouldBe(request.SystemTraceCtx);
        reply.UserTraceCtx.ShouldBe(request.UserTraceCtx);
    }

    [Fact]
    public async Task Reports_steps_as_not_completed()
    {
        var options = WorkerHost.Orders with { Id = "checkout" };
        await using var worker = new WorkerHost(_gateway, functions: inngest => inngest.AddFunction(options, static async (context, _) =>
        {
            var total = await context.Step.RunAsync("total", () => 42);
            return new { total };
        }));
        await worker.StartAsync();
        var session = await _gateway.NextReadySessionAsync();

        await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, GatewayMessages.Request("req-1", "shop-checkout"));

        var reply = await session.NextAsync(GatewayMessageType.WorkerReply, SDKResponse.Parser);
        reply.Status.ShouldBe(SDKResponseStatus.NotCompleted);
        using var operations = JsonDocument.Parse(reply.Body.Memory);
        var step = operations.RootElement.EnumerateArray().ShouldHaveSingleItem();
        step.GetProperty("op").GetString().ShouldBe("StepRun");
        step.GetProperty("name").GetString().ShouldBe("total");
        step.GetProperty("data").GetInt32().ShouldBe(42);
    }

    [Fact]
    public async Task Hands_the_payload_and_step_to_the_engine_as_an_http_call_would()
    {
        var engine = new ControlledEngine();
        await using var worker = new WorkerHost(_gateway, engine: engine);
        await worker.StartAsync();
        var session = await _gateway.NextReadySessionAsync();

        var request = GatewayMessages.Request("req-1", stepId: "e7d8a2f140845095749d60246ff1110c9d01d76a");
        request.JobId = "job-1";
        await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, request);

        var execution = await engine.NextAsync();
        execution.Body.ShouldBe(RequestPayloads.Simple);
        execution.Request.StepId.ShouldBe("e7d8a2f140845095749d60246ff1110c9d01d76a");
        execution.Request.RequestId.ShouldBe("req-1");
        execution.Request.JobId.ShouldBe("job-1");
        execution.Request.Function.Id.ShouldBe("orders");
        execution.Request.Services.ShouldNotBeSameAs(worker.Services);
        execution.Complete(200, "null");
        await session.NextAsync(GatewayMessageType.WorkerReply);
    }

    // The call's span continues this trace context when nothing listens for the worker's span of the request.
    [Fact]
    public async Task Hands_the_trace_context_of_the_request_to_the_engine()
    {
        var engine = new ControlledEngine();
        await using var worker = new WorkerHost(_gateway, engine: engine);
        await worker.StartAsync();
        var session = await _gateway.NextReadySessionAsync();

        await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, GatewayMessages.Request("req-1"));

        var execution = await engine.NextAsync();
        execution.Request.TraceParent.ShouldBe("00-user-01");
        execution.Request.TraceState.ShouldBe("user=1");
        execution.Complete(200, "null");
        await session.NextAsync(GatewayMessageType.WorkerReply);
    }

    [Fact]
    public async Task A_request_without_a_step_id_starts_the_run()
    {
        var engine = new ControlledEngine();
        await using var worker = new WorkerHost(_gateway, engine: engine);
        await worker.StartAsync();
        var session = await _gateway.NextReadySessionAsync();

        await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, GatewayMessages.Request("req-1", stepId: null));

        var ack = await session.NextAsync(GatewayMessageType.WorkerRequestAck, WorkerRequestAckData.Parser);
        ack.HasStepId.ShouldBeFalse();
        (await engine.NextAsync()).Request.StepId.ShouldBeNull();
    }

    // SDKResponseStatus: 0 NOT_COMPLETED, 1 DONE, 2 ERROR.
    [Theory]
    [InlineData(500, false, "30", 2, false)]
    [InlineData(400, true, null, 2, true)]
    [InlineData(206, null, null, 0, false)]
    [InlineData(206, true, null, 0, true)]
    [InlineData(200, null, null, 1, false)]
    public async Task Maps_the_engine_result_to_the_reply(int statusCode, bool? noRetry, string? retryAfter, int status, bool replyNoRetry)
    {
        var engine = new ControlledEngine();
        await using var worker = new WorkerHost(_gateway, engine: engine);
        await worker.StartAsync();
        var session = await _gateway.NextReadySessionAsync();
        await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, GatewayMessages.Request("req-1"));

        (await engine.NextAsync()).Complete(statusCode, """{"name":"Error","message":"boom"}""", noRetry, retryAfter);

        var reply = await session.NextAsync(GatewayMessageType.WorkerReply, SDKResponse.Parser);
        reply.Status.ShouldBe((SDKResponseStatus)status);
        reply.NoRetry.ShouldBe(replyNoRetry);
        reply.HasRetryAfter.ShouldBe(retryAfter is not null);
        reply.RetryAfter.ShouldBe(retryAfter ?? string.Empty);
        reply.Body.ToStringUtf8().ShouldBe("""{"name":"Error","message":"boom"}""");
    }

    [Fact]
    public async Task Runs_requests_side_by_side()
    {
        var engine = new ControlledEngine();
        await using var worker = new WorkerHost(_gateway, engine: engine);
        await worker.StartAsync();
        var session = await _gateway.NextReadySessionAsync();

        var fastPayload = RequestPayloads.Simple.Replace("\"orderId\":42", "\"orderId\":7", StringComparison.Ordinal);
        await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, GatewayMessages.Request("slow"));
        await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, GatewayMessages.Request("fast", payload: fastPayload));
        ControlledExecution[] running = [await engine.NextAsync(), await engine.NextAsync()];
        var slow = running.Single(execution => execution.Body == RequestPayloads.Simple);
        var fast = running.Single(execution => execution.Body == fastPayload);

        // Both are running now; the one that finishes first is answered first.
        fast.Complete(200, "\"fast\"");
        (await session.NextAsync(GatewayMessageType.WorkerReply, SDKResponse.Parser)).RequestId.ShouldBe("fast");
        slow.Complete(200, "\"slow\"");
        (await session.NextAsync(GatewayMessageType.WorkerReply, SDKResponse.Parser)).RequestId.ShouldBe("slow");
    }

    [Fact]
    public async Task Leaves_a_request_for_another_app_unacknowledged()
    {
        var engine = new ControlledEngine();
        await using var worker = new WorkerHost(_gateway, engine: engine);
        await worker.StartAsync();
        var session = await _gateway.NextReadySessionAsync();

        await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, GatewayMessages.Request("req-1", appName: "billing"));
        await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, GatewayMessages.Request("req-2"));

        // Messages arrive in order, so the acknowledgement of the second request shows the first was skipped. The
        // execution starts after the acknowledgement, so wait for it rather than expect it already.
        (await session.NextAsync(GatewayMessageType.WorkerRequestAck, WorkerRequestAckData.Parser)).RequestId.ShouldBe("req-2");
        var execution = await engine.NextAsync();
        execution.Request.Function.Id.ShouldBe("orders");
        engine.TryTake(out _).ShouldBeFalse();
        worker.LogsWithId(512).ShouldHaveSingleItem().Message.ShouldContain("billing");
        execution.Complete(200, "null");
    }

    [Fact]
    public async Task Answers_a_request_for_an_unknown_function_with_an_error()
    {
        await using var worker = new WorkerHost(_gateway);
        await worker.StartAsync();
        var session = await _gateway.NextReadySessionAsync();

        await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, GatewayMessages.Request("req-1", "shop-refunds"));

        await session.NextAsync(GatewayMessageType.WorkerRequestAck);
        var reply = await session.NextAsync(GatewayMessageType.WorkerReply, SDKResponse.Parser);
        reply.Status.ShouldBe(SDKResponseStatus.Error);
        reply.NoRetry.ShouldBeFalse();
        using var error = JsonDocument.Parse(reply.Body.Memory);
        error.RootElement.GetProperty("message").GetString().ShouldBe("Function \"shop-refunds\" was not found in this app; is the app synced correctly?");
        worker.LogsWithId(515).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Answers_with_an_error_when_the_engine_itself_fails()
    {
        var engine = new ControlledEngine();
        await using var worker = new WorkerHost(_gateway, engine: engine);
        await worker.StartAsync();
        var session = await _gateway.NextReadySessionAsync();
        await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, GatewayMessages.Request("req-1"));

        (await engine.NextAsync()).Fail(new InvalidOperationException("The engine broke"));

        var reply = await session.NextAsync(GatewayMessageType.WorkerReply, SDKResponse.Parser);
        reply.Status.ShouldBe(SDKResponseStatus.Error);
        using var error = JsonDocument.Parse(reply.Body.Memory);
        error.RootElement.GetProperty("type").GetString().ShouldBe("internal");
        error.RootElement.GetProperty("name").GetString().ShouldBe("InvalidOperationException");
        error.RootElement.GetProperty("message").GetString().ShouldBe("The engine broke");
        worker.LogsWithId(516).ShouldHaveSingleItem().Exception.ShouldBeOfType<InvalidOperationException>();
    }

    // The gateway reads at most 8 MiB in a message and closes the connection on a larger one, losing the
    // acknowledgements and lease extensions queued behind it (pkg/connect/gateway.go). Over HTTP, Inngest fails a
    // response that large as output_too_large, and the function would answer the same again.
    [Fact]
    public async Task A_reply_over_8_MiB_fails_the_call_without_retries_instead_of_going_out()
    {
        var engine = new ControlledEngine();
        await using var worker = new WorkerHost(_gateway, engine: engine);
        await worker.StartAsync();
        var session = await _gateway.NextReadySessionAsync();
        await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, GatewayMessages.Request("req-1"));

        (await engine.NextAsync()).Complete(200, $"\"{new string('x', 8 * 1024 * 1024)}\"");

        var reply = await session.NextAsync(GatewayMessageType.WorkerReply, SDKResponse.Parser);
        reply.RequestId.ShouldBe("req-1");
        reply.Status.ShouldBe(SDKResponseStatus.Error);
        reply.NoRetry.ShouldBeTrue();
        using var error = JsonDocument.Parse(reply.Body.Memory);
        error.RootElement.GetProperty("name").GetString().ShouldBe("NonRetriableException");
        error.RootElement.GetProperty("message").GetString().ShouldBe(
            "Function \"shop-orders\" answered Inngest with 8.01 MiB, more than the 8 MiB Inngest accepts in one answer. " +
            "Keep large data outside the run and return a reference to it.");
        worker.LogsWithId(527).ShouldHaveSingleItem().Message.ShouldContain("req-1");
    }

    [Fact]
    public async Task A_large_reply_within_8_MiB_goes_out_as_it_is()
    {
        var engine = new ControlledEngine();
        await using var worker = new WorkerHost(_gateway, engine: engine);
        await worker.StartAsync();
        var session = await _gateway.NextReadySessionAsync();
        await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, GatewayMessages.Request("req-1"));
        var output = $"\"{new string('x', (8 * 1024 * 1024) - 1024)}\"";

        (await engine.NextAsync()).Complete(200, output);

        var reply = await session.NextAsync(GatewayMessageType.WorkerReply, SDKResponse.Parser);
        reply.Status.ShouldBe(SDKResponseStatus.Done);
        reply.Body.Length.ShouldBe(output.Length);
        worker.LogsWithId(527).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_scoped_service_that_throws_while_disposed_does_not_turn_the_reply_into_an_error()
    {
        await using var worker = new WorkerHost(
            _gateway,
            functions: builder => builder.AddFunction(WorkerHost.Orders, static (context, _) =>
            {
                context.Services.GetRequiredService<ThrowsOnDispose>();
                return Task.FromResult<object?>(new { received = 42 });
            }),
            configure: services => services.AddScoped<ThrowsOnDispose>());
        await worker.StartAsync();
        var session = await _gateway.NextReadySessionAsync();

        await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, GatewayMessages.Request("req-1"));

        var reply = await session.NextAsync(GatewayMessageType.WorkerReply, SDKResponse.Parser);
        reply.Status.ShouldBe(SDKResponseStatus.Done);
        reply.Body.ToStringUtf8().ShouldBe("""{"received":42}""");
        worker.LogsWithId(536).ShouldHaveSingleItem().Exception.ShouldBeOfType<InvalidOperationException>();
    }

    [Theory]
    [InlineData(2)]
    [InlineData(7)]
    public async Task Reads_a_request_that_arrives_in_several_frames(int fragments)
    {
        await using var worker = new WorkerHost(_gateway);
        await worker.StartAsync();
        var session = await _gateway.NextReadySessionAsync();
        var message = FrameTestExtensions.Encode(GatewayMessageType.GatewayExecutorRequest, GatewayMessages.Request("req-1"));

        await session.SendFramesAsync(message, fragments);

        (await session.NextAsync(GatewayMessageType.WorkerReply, SDKResponse.Parser)).Body.ToStringUtf8().ShouldBe("""{"received":42}""");
    }

    [Fact]
    public async Task Reads_a_request_of_several_megabytes()
    {
        var engine = new ControlledEngine();
        await using var worker = new WorkerHost(_gateway, engine: engine);
        await worker.StartAsync();
        var session = await _gateway.NextReadySessionAsync();
        var payload = RequestPayloads.Simple.Replace("\"orderId\":42", $"\"orderId\":42,\"blob\":\"{new string('x', 3 * 1024 * 1024)}\"", StringComparison.Ordinal);

        await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, GatewayMessages.Request("req-1", payload: payload));

        var execution = await engine.NextAsync();
        execution.Body.ShouldBe(payload);
        execution.Complete(200, "null");
        await session.NextAsync(GatewayMessageType.WorkerReply);
    }

    [Fact]
    public async Task Ignores_a_message_that_is_not_protobuf_and_keeps_going()
    {
        await using var worker = new WorkerHost(_gateway);
        await worker.StartAsync();
        var session = await _gateway.NextReadySessionAsync();

        await session.SendFramesAsync([0x12, 0x7F, 0x01], fragments: 1);
        await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, GatewayMessages.Request("req-1"));

        await session.NextAsync(GatewayMessageType.WorkerReply);
        worker.LogsWithId(530).ShouldHaveSingleItem();
    }

    private sealed class ThrowsOnDispose : IDisposable
    {
        public void Dispose() => throw new InvalidOperationException("Disposing failed");
    }
}
