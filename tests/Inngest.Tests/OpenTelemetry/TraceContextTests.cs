using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Google.Protobuf;
using Inngest.Connect.Protocol;
using Inngest.Tests.Connect.Support;
using Inngest.Tests.Execution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Inngest.Tests.OpenTelemetry;

// A call continues the trace Inngest sent with it: over HTTP through ASP.NET Core's request span, which reads the
// traceparent header, and over Connect through the worker's span for the request, which reads user_trace_ctx.
public sealed class TraceContextTests
{
    private const string TraceId = "0af7651916cd43dd8448eb211c80319c";
    private const string ParentSpanId = "b7ad6b7169203331";

    // As the dev server sends it, with Inngest's ids for the function and the app.
    private const string TraceState =
        "inngest@traceref=%7B%22tp%22%3A%22%22%2C%22ts%22%3A%22%22%7D,inngest@fn=2b6a2c8e-8752-56c4-bafe-1d61d4780711,inngest@app=34ee71cf-d26b-5925-a750-eb95b9daf65f";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Over_http_the_call_continues_the_trace_of_the_traceparent_header()
    {
        var spans = new ConcurrentQueue<Activity>();
        using var listener = Listen(spans, runId: null, "inngest", "Microsoft.AspNetCore");
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.Services
            .AddInngest(options =>
            {
                options.AppId = "shop";
                options.IsDev = true;
            })
            .AddFunction(new FunctionOptions { Id = "fn", Triggers = [Trigger.Event("test/event")] }, static (_, _) => Task.FromResult<object?>("done"))
            .AddInstrumentation();
        await using var app = builder.Build();
        app.MapInngest();
        await app.StartAsync(Cancellation);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/inngest?fnId=shop-fn&stepId=step") { Content = new ByteArrayContent(Call.New().ToBytes()) };
        request.Headers.Add("traceparent", $"00-{TraceId}-{ParentSpanId}-01");
        request.Headers.Add("tracestate", TraceState);

        using var response = await app.GetTestClient().SendAsync(request, Cancellation);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await Poll.UntilAsync(() => spans.Count == 2);
        var server = spans.Single(span => span.Source.Name == "Microsoft.AspNetCore");
        var call = spans.Single(span => span.OperationName == "inngest.execution");
        server.ParentSpanId.ToHexString().ShouldBe(ParentSpanId);
        call.ParentSpanId.ShouldBe(server.SpanId);
        call.GetTagItem("sys.function.id").ShouldBe("2b6a2c8e-8752-56c4-bafe-1d61d4780711");
        call.GetTagItem("sys.app.id").ShouldBe("34ee71cf-d26b-5925-a750-eb95b9daf65f");
    }

    [Fact]
    public async Task Over_connect_the_call_continues_the_trace_of_the_executor_request()
    {
        var spans = new ConcurrentQueue<Activity>();
        var runId = UniqueRunId();
        using var listener = Listen(spans, runId, "inngest");
        await using var gateway = await FakeGateway.StartAsync();
        var worker = new WorkerHost(gateway, functions: Instrumented);
        await using (worker)
        {
            await worker.StartAsync();
            var session = await gateway.NextReadySessionAsync();
            var executorRequest = GatewayMessages.Request("req-1");
            executorRequest.RunId = runId;
            executorRequest.UserTraceCtx = ByteString.CopyFromUtf8($$"""{"traceparent":"00-{{TraceId}}-{{ParentSpanId}}-01","tracestate":"{{TraceState}}"}""");

            await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, executorRequest);
            await session.NextAsync(GatewayMessageType.WorkerReply);
        }

        var connect = spans.Single(span => span.OperationName == "inngest.connect.request");
        var call = spans.Single(span => span.OperationName == "inngest.execution");
        connect.Kind.ShouldBe(ActivityKind.Server);
        connect.HasRemoteParent.ShouldBeTrue();
        connect.ParentSpanId.ToHexString().ShouldBe(ParentSpanId);
        connect.TraceStateString.ShouldBe(TraceState);
        connect.GetTagItem("sys.function.slug").ShouldBe("shop-orders");
        connect.GetTagItem("sdk.run.id").ShouldBe(runId);
        call.ParentSpanId.ShouldBe(connect.SpanId);
        call.GetTagItem("sys.function.slug").ShouldBe("shop-orders");
        call.GetTagItem("sys.function.id").ShouldBe("2b6a2c8e-8752-56c4-bafe-1d61d4780711");
        call.GetTagItem("sys.app.id").ShouldBe("34ee71cf-d26b-5925-a750-eb95b9daf65f");
    }

    [Theory]
    [InlineData("")]
    [InlineData("""{"traceparent":"00-not-a-trace"}""")]
    [InlineData("""{"traceparent":7,"tracestate":["a"],"other":{"traceparent":"x"}}""")]
    [InlineData("""{"traceparent":""")]
    public async Task Over_connect_a_request_without_a_usable_trace_context_starts_a_trace(string userTraceContext)
    {
        var spans = new ConcurrentQueue<Activity>();
        var runId = UniqueRunId();
        using var listener = Listen(spans, runId, "inngest");
        await using var gateway = await FakeGateway.StartAsync();
        var worker = new WorkerHost(gateway, functions: Instrumented);
        await using (worker)
        {
            await worker.StartAsync();
            var session = await gateway.NextReadySessionAsync();
            var executorRequest = GatewayMessages.Request("req-1");
            executorRequest.RunId = runId;
            executorRequest.UserTraceCtx = ByteString.CopyFromUtf8(userTraceContext);

            await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, executorRequest);
            (await session.NextAsync(GatewayMessageType.WorkerReply, SDKResponse.Parser)).Status.ShouldBe(SDKResponseStatus.Done);
        }

        var connect = spans.Single(span => span.OperationName == "inngest.connect.request");
        connect.ParentSpanId.ShouldBe(default);
        connect.HasRemoteParent.ShouldBeFalse();
    }

    private static void Instrumented(IInngestBuilder inngest) =>
        inngest
            .AddFunction(WorkerHost.Orders, static (_, _) => Task.FromResult<object?>("done"))
            .AddInstrumentation();

    // Other Connect tests send requests of the same run at the same time; a run id of its own sets a test's apart.
    private static string UniqueRunId() => $"01JRUN{Guid.NewGuid():N}";

    // Keeps the spans of this test: those in its trace, and those of its run when the request started a new trace.
    private static ActivityListener Listen(ConcurrentQueue<Activity> spans, string? runId, params string[] sources)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => sources.Contains(source.Name),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.TraceId.ToHexString() == TraceId || (runId is not null && Equals(activity.GetTagItem("sdk.run.id"), runId)))
                {
                    spans.Enqueue(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
