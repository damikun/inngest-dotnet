using Inngest.Tests.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sentry;
using Sentry.Extensibility;

namespace Inngest.Tests.Sentry;

// The scope and the trace a call reports in. The trace ids are what the TypeScript middleware derives from the same run
// ids.
[Collection(SentryHost.Collection)]
public sealed class ScopeTests
{
    private const string RunTraceId = "019341f2cd3dba303a1ea2b1346da38b";
    private const string OtherRunId = "01JD0Z5K9XQ8R3M7N2P4T6V8WC";
    private const string OtherRunTraceId = "019341f2cd3dba303a1ea2b1346da38c";

    [Fact]
    public async Task Every_call_of_a_run_reports_to_the_trace_of_the_run_and_other_runs_to_their_own()
    {
        await using var host = new SentryHost(options => options.OnlyCaptureFinalAttempt = false);
        var function = TestFunction.Define(_ => throw new InvalidOperationException("boom"));

        await host.CallAsync(function, Call.New().Attempt(0, maxAttempts: 4));
        await host.CallAsync(function, Call.New().Attempt(1, maxAttempts: 4));
        await host.CallAsync(function, Call.New().Attempt(0, maxAttempts: 4), runId: OtherRunId);

        var traces = host.Events.Select(report => report.Contexts.Trace.TraceId.ToString()).ToList();
        traces.ShouldBe([RunTraceId, RunTraceId, OtherRunTraceId]);
        host.Events.ShouldAllBe(report => report.Contexts.Trace.ParentSpanId == SpanId.Parse("1000000000000000"));
    }

    [Fact]
    public async Task The_event_joins_the_trace_of_the_run_and_not_that_of_the_request_carrying_the_call()
    {
        await using var host = new SentryHost();
        var function = TestFunction.Define(_ => throw new NonRetriableException("no"));
        var hub = HubAdapter.Instance;
        var request = hub.StartTransaction("POST /api/inngest", "http.server");

        // As Sentry.AspNetCore sets the request up: its own scope, locked, with the request's transaction.
        using (hub.PushAndLockScope())
        {
            hub.ConfigureScope(scope => scope.Transaction = request);

            await host.CallAsync(function, Call.New());

            hub.ConfigureScope(scope => scope.Transaction.ShouldBeSameAs(request));
        }

        request.Finish();
        var report = host.Events.ShouldHaveSingleItem();
        report.Contexts.Trace.TraceId.ToString().ShouldBe(RunTraceId);
        report.Contexts.Trace.TraceId.ShouldNotBe(request.TraceId);
        report.TransactionName.ShouldBe("inngest:fn");
        request.Name.ShouldBe("POST /api/inngest");
    }

    [Fact]
    public async Task What_the_function_and_its_steps_report_themselves_carries_the_tags_of_the_call()
    {
        await using var host = new SentryHost();
        var function = TestFunction.Define(async context =>
        {
            SentrySdk.CaptureMessage("from the function");
            return await context.Step.RunAsync("charge", () =>
            {
                SentrySdk.CaptureMessage("from the step");
                return 42;
            });
        });

        await host.CallAsync(function, Call.New().Attempt(1, maxAttempts: 4));
        SentrySdk.CaptureMessage("after the call");

        var messages = host.Events.ToDictionary(report => report.Message!.Message!, StringComparer.Ordinal);
        foreach (var name in new[] { "from the function", "from the step" })
        {
            var tags = TagsOf(messages[name]);
            tags["inngest.run.id"].ShouldBe(SentryHost.RunId);
            tags["inngest.function.id"].ShouldBe("shop-fn");
            tags["inngest.attempt"].ShouldBe("1");
            messages[name].Contexts.Trace.TraceId.ToString().ShouldBe(RunTraceId);
        }

        TagsOf(messages["after the call"]).Keys.ShouldNotContain(key => key.StartsWith("inngest.", StringComparison.Ordinal));
    }

    // Sentry.AspNetCore locks the scope of a request, so a scope pushed inside it is the request's, and the tags stay
    // on it for the engine's own log entries, which become events when the logging integration is on.
    [Fact]
    public async Task In_a_request_scope_the_tags_also_reach_the_event_of_the_logging_integration()
    {
        await using var host = new SentryHost(services: services => services.AddSingleton<ILoggerProvider>(new ErrorsToSentry()));
        var function = TestFunction.Define(_ => throw new NonRetriableException("no"));

        using (HubAdapter.Instance.PushAndLockScope())
        {
            (await host.CallAsync(function, Call.New())).ShouldBe(400);
        }

        // One event for the exception, which is the log entry's: Sentry drops the second report of the same exception.
        var report = host.Events.ShouldHaveSingleItem();
        TagsOf(report)["inngest.run.id"].ShouldBe(SentryHost.RunId);
    }

    private static Dictionary<string, string> TagsOf(SentryEvent report) =>
        report.Tags.ToDictionary(tag => tag.Key, tag => tag.Value, StringComparer.Ordinal);

    // What Sentry's logging integration does with an error that has an exception.
    private sealed class ErrorsToSentry : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error && exception is not null)
            {
                HubAdapter.Instance.CaptureException(exception);
            }
        }

        public void Dispose()
        {
        }
    }
}
