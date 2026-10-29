using System.Collections.Concurrent;
using System.Diagnostics;
using Inngest.IntegrationTests.Functions;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.Connect;

// The worker's span for an executor request continues the trace the dev server sends in user_trace_ctx.
[Collection(ConnectCollection.Name)]
public sealed class ConnectTracingTests(ConnectFixture fixture) : IAsyncLifetime
{
    private const string AppId = "itests-connect-tracing";
    private const string Slug = "itests-connect-tracing-traced";

    private readonly ConcurrentQueue<Activity> _spans = new();
    private ActivityListener? _listener;
    private ConnectWorkerApp? _worker;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "inngest",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (Equals(activity.GetTagItem("sys.function.slug"), Slug))
                {
                    _spans.Enqueue(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(_listener);
        _worker = await ConnectWorkerApp.StartAsync(
            fixture.ServerUrl,
            AppId,
            inngest => inngest.AddFunction(
                new FunctionOptions { Id = "traced", Triggers = [Trigger.Event("itests/connect.traced")] },
                static (_, _) => Task.FromResult<object?>("traced")),
            Cancellation);
        await fixture.Api.WaitForFunctionsAsync(AppId, [Slug], Cancellation);
    }

    public async ValueTask DisposeAsync()
    {
        if (TestContext.Current.TestState?.Result == TestResult.Failed && TestContext.Current.TestOutputHelper is { } output)
        {
            output.WriteLine(await fixture.DescribeAsync(CancellationToken.None));
        }

        if (_worker is not null)
        {
            await _worker.DisposeAsync();
        }

        _listener?.Dispose();
    }

    [Fact(Timeout = 60_000)]
    public async Task The_span_of_an_executor_request_continues_the_trace_inngest_sent()
    {
        var eventId = await fixture.Api.SendEventAsync("itests/connect.traced", new TestEvent(Guid.NewGuid().ToString("N")), Cancellation);
        var run = await fixture.Api.WaitForRunEndAsync(eventId, Cancellation, TimeSpan.FromSeconds(30));

        run.Status.ShouldBe(RunStatus.Completed);
        var found = await Poll.UntilAsync(
            "the worker's span for the request",
            _ => Task.FromResult(_spans.FirstOrDefault()),
            span => span is not null,
            TimeSpan.FromSeconds(10),
            describe: null,
            Cancellation);
        var span = found.ShouldNotBeNull();
        span.Kind.ShouldBe(ActivityKind.Server);
        span.HasRemoteParent.ShouldBeTrue();
        span.ParentSpanId.ShouldNotBe(default);
        span.GetTagItem("sdk.run.id").ShouldBe(run.RunId);

        // Inngest's ids for the function and the app, which the call's span of Inngest.OpenTelemetry reads.
        span.TraceStateString.ShouldNotBeNull().ShouldContain("inngest@fn=");
        span.TraceStateString.ShouldContain("inngest@app=");
    }
}
