using Inngest.Tests.Execution;
using Sentry;

namespace Inngest.Tests.Sentry;

// A transaction for every call and a span for every step it executes, in the trace of the run, as the TypeScript
// middleware's request and step spans.
[Collection(SentryHost.Collection)]
public sealed class SpanTests
{
    private const string RunTraceId = "019341f2cd3dba303a1ea2b1346da38b";

    [Fact]
    public async Task A_call_is_a_transaction_in_the_trace_of_the_run_and_its_step_a_span_of_it()
    {
        await using var host = new SentryHost();
        var function = TestFunction.Define(async context => await context.Step.RunAsync(new StepOptions("charge") { Name = "Charge the card" }, () => 42));

        (await host.CallAsync(function, Call.New().Attempt(1, maxAttempts: 4))).ShouldBe(206);

        var transaction = host.Transactions.ShouldHaveSingleItem();
        transaction.Name.ShouldBe("inngest:fn");
        transaction.Operation.ShouldBe("request");
        transaction.Status.ShouldBe(SpanStatus.Ok);
        transaction.TraceId.ToString().ShouldBe(RunTraceId);
        transaction.ParentSpanId.ShouldBe(SpanId.Parse("1000000000000000"));
        transaction.Tags["inngest.run.id"].ShouldBe(SentryHost.RunId);
        transaction.Tags["inngest.function.id"].ShouldBe("shop-fn");
        transaction.Tags["inngest.attempt"].ShouldBe("1");
        transaction.Tags["inngest.event.name"].ShouldBe("test/event");

        var step = transaction.Spans.ShouldHaveSingleItem();
        step.Operation.ShouldBe("step");
        step.Description.ShouldBe("Charge the card");
        step.Status.ShouldBe(SpanStatus.Ok);
        step.ParentSpanId.ShouldBe(transaction.SpanId);
        step.Tags["inngest.step.name"].ShouldBe("Charge the card");
        step.Tags["inngest.step.type"].ShouldBe("Run");
    }

    [Fact]
    public async Task Every_call_of_a_run_is_a_transaction_of_its_own_in_the_same_trace()
    {
        await using var host = new SentryHost();
        var function = TestFunction.Define(async context =>
        {
            var a = await context.Step.RunAsync("a", () => 1);
            return await context.Step.RunAsync("b", () => a + 1);
        });

        await host.CallAsync(function, Call.New());
        await host.CallAsync(function, Call.New().Data("a", "1"));
        await host.CallAsync(function, Call.New().Data("a", "1").Data("b", "2"));

        host.Transactions.Count.ShouldBe(3);
        host.Transactions.ShouldAllBe(transaction => transaction.TraceId.ToString() == RunTraceId);
        host.Transactions.Select(transaction => transaction.Spans.Count).ShouldBe([1, 1, 0]);
        host.Transactions.Select(transaction => transaction.Spans.SingleOrDefault()?.Description).ShouldBe(["a", "b", null]);
    }

    [Fact]
    public async Task A_failed_step_fails_its_span_and_the_transaction_and_its_report_is_linked_to_the_span()
    {
        await using var host = new SentryHost(options =>
        {
            options.CaptureStepErrors = true;
            options.OnlyCaptureFinalAttempt = false;
        });
        var function = TestFunction.Define(async context =>
            await context.Step.RunAsync<int>("charge", _ => throw new TimeoutException("slow")));

        await host.CallAsync(function, Call.New().Attempt(0, maxAttempts: 4));

        var transaction = host.Transactions.ShouldHaveSingleItem();
        transaction.Status.ShouldBe(SpanStatus.InternalError);
        var step = transaction.Spans.ShouldHaveSingleItem();
        step.Status.ShouldBe(SpanStatus.InternalError);
        var report = host.Events.ShouldHaveSingleItem();
        report.Contexts.Trace.SpanId.ShouldBe(step.SpanId);
        report.Contexts.Trace.TraceId.ToString().ShouldBe(RunTraceId);
    }

    [Fact]
    public async Task The_report_of_a_failed_function_is_linked_to_the_transaction()
    {
        await using var host = new SentryHost();
        var function = TestFunction.Define(_ => throw new NonRetriableException("no"));

        await host.CallAsync(function, Call.New());

        var transaction = host.Transactions.ShouldHaveSingleItem();
        transaction.Status.ShouldBe(SpanStatus.InternalError);
        var report = host.Events.ShouldHaveSingleItem();
        report.Contexts.Trace.SpanId.ShouldBe(transaction.SpanId);
        report.Contexts.Trace.ParentSpanId.ShouldBe(SpanId.Parse("1000000000000000"));
    }

    [Fact]
    public async Task Nothing_is_started_while_Sentry_is_not_enabled()
    {
        await using var host = new SentryHost(initialize: false, registerHub: true);
        var function = TestFunction.Define(async context => await context.Step.RunAsync("charge", () => 42));

        await host.CallAsync(function, Call.New());

        host.Spy!.Methods.ShouldNotContain("StartTransaction");
    }
}
