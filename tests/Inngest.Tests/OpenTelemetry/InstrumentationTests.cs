using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inngest.Client;
using Inngest.Functions;
using Inngest.OpenTelemetry;
using Inngest.Tests.Checkpointing;
using Inngest.Tests.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Tests.OpenTelemetry;

public sealed class InstrumentationTests
{
    [Fact]
    public async Task A_call_that_runs_a_step_has_a_span_and_the_step_a_span_under_it()
    {
        await using var host = new TelemetryHost();
        var function = TestFunction.Define(async context => await context.Step.RunAsync("charge", () => 42));

        (await host.CallAsync(function, Call.New())).ShouldBe(206);

        var call = host.Span("inngest.execution");
        var step = host.Span("inngest.step");
        call.Source.Name.ShouldBe("inngest");
        call.Kind.ShouldBe(ActivityKind.Internal);
        call.Status.ShouldBe(ActivityStatusCode.Unset);
        TagsOf(call).ShouldBe(new Dictionary<string, object?>
        {
            ["sys.function.slug"] = "shop-fn",
            ["sdk.run.id"] = host.RunId,
            ["inngest.attempt"] = 0,
            ["inngest.event.name"] = "test/event",
            ["inngest.event.id"] = "01JTEST",
            ["inngest.memoized_steps"] = 0,
        });
        step.ParentSpanId.ShouldBe(call.SpanId);
        step.TraceId.ShouldBe(call.TraceId);
        TagsOf(step).ShouldBe(new Dictionary<string, object?>
        {
            ["inngest.step.id"] = "charge",
            ["inngest.step.type"] = "run",
            ["inngest.step.attempt"] = 0,
            ["sdk.run.id"] = host.RunId,
            ["sys.function.slug"] = "shop-fn",
        });
    }

    [Fact]
    public async Task A_replay_has_no_spans_for_memoized_steps_and_counts_them_on_the_call()
    {
        await using var host = new TelemetryHost();
        var function = TestFunction.Define(async context =>
        {
            var a = await context.Step.RunAsync("a", () => 1);
            var b = await context.Step.RunAsync("b", () => 2);
            return a + b;
        });

        (await host.CallAsync(function, Call.New().Data("a", "1").Data("b", "2"))).ShouldBe(200);

        host.Spans.Select(span => span.OperationName).ShouldBe(["inngest.execution"]);
        host.Span("inngest.execution").GetTagItem("inngest.memoized_steps").ShouldBe(2);
        host.Measured("inngest.step.duration").ShouldBeEmpty();
    }

    [Fact]
    public async Task Activities_of_the_function_and_of_a_step_nest_under_the_call_and_the_step()
    {
        await using var host = new TelemetryHost();
        var source = host.TestSource;
        var function = TestFunction.Define(async context =>
        {
            using (source.StartActivity("in-function"))
            {
            }

            return await context.Step.RunAsync("charge", () =>
            {
                using var activity = source.StartActivity("in-step");
                return 42;
            });
        });

        await host.CallAsync(function, Call.New());

        host.Span("in-function").ParentSpanId.ShouldBe(host.Span("inngest.execution").SpanId);
        host.Span("in-step").ParentSpanId.ShouldBe(host.Span("inngest.step").SpanId);
    }

    [Fact]
    public async Task The_call_span_is_a_child_of_the_activity_the_host_runs_the_call_in()
    {
        await using var host = new TelemetryHost();
        var function = TestFunction.Define(_ => Task.FromResult<object?>("done"));
        using var request = host.TestSource.StartActivity("POST /api/inngest", ActivityKind.Server)!;

        await host.CallAsync(function, Call.New());

        var call = host.Span("inngest.execution");
        call.ParentSpanId.ShouldBe(request.SpanId);
        call.TraceId.ShouldBe(request.TraceId);
        Activity.Current.ShouldBeSameAs(request);
    }

    [Fact]
    public async Task A_step_that_throws_marks_its_span_as_failed_and_counts_a_retry()
    {
        await using var host = new TelemetryHost();
        var function = TestFunction.Define(async context =>
            await context.Step.RunAsync<int>("charge", _ => throw new TimeoutException("slow")));

        (await host.CallAsync(function, Call.New().Attempt(0, maxAttempts: 4))).ShouldBe(206);

        var step = host.Span("inngest.step");
        step.Status.ShouldBe(ActivityStatusCode.Error);
        step.StatusDescription.ShouldBe("slow");
        step.GetTagItem("error.type").ShouldBe("System.TimeoutException");
        var exception = step.Events.ShouldHaveSingleItem();
        exception.Name.ShouldBe("exception");
        exception.Tags.ShouldContain(new KeyValuePair<string, object?>("exception.type", "System.TimeoutException"));
        exception.Tags.ShouldContain(new KeyValuePair<string, object?>("exception.message", "slow"));
        host.Span("inngest.execution").Status.ShouldBe(ActivityStatusCode.Unset);

        host.Measured("inngest.retries").ShouldHaveSingleItem().Tags.ShouldBe(new Dictionary<string, object?>
        {
            ["sys.function.slug"] = "shop-fn",
            ["inngest.step.type"] = "run",
            ["error.type"] = "System.TimeoutException",
        });
        host.Measured("inngest.step.duration").ShouldHaveSingleItem().Tags["error.type"].ShouldBe("System.TimeoutException");
        host.Measured("inngest.run.failed").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_function_that_fails_for_good_marks_the_call_and_counts_a_failed_run()
    {
        await using var host = new TelemetryHost();
        var function = TestFunction.Define(_ => throw new NonRetriableException("no"));

        (await host.CallAsync(function, Call.New())).ShouldBe(400);

        var call = host.Span("inngest.execution");
        call.Status.ShouldBe(ActivityStatusCode.Error);
        call.StatusDescription.ShouldBe("no");
        call.GetTagItem("error.type").ShouldBe("Inngest.NonRetriableException");
        call.Events.ShouldHaveSingleItem().Name.ShouldBe("exception");
        host.Measured("inngest.run.failed").ShouldHaveSingleItem().Tags.ShouldBe(new Dictionary<string, object?>
        {
            ["sys.function.slug"] = "shop-fn",
            ["error.type"] = "Inngest.NonRetriableException",
        });
        host.Measured("inngest.execution.duration").ShouldHaveSingleItem().Tags["error.type"].ShouldBe("Inngest.NonRetriableException");
        host.Measured("inngest.retries").ShouldBeEmpty();
        host.Measured("inngest.run.completed").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_function_error_that_inngest_retries_counts_a_retry_and_no_failed_run()
    {
        await using var host = new TelemetryHost();
        var function = TestFunction.Define(_ => throw new InvalidOperationException("boom"));

        (await host.CallAsync(function, Call.New().Attempt(1, maxAttempts: 4))).ShouldBe(500);

        host.Measured("inngest.retries").ShouldHaveSingleItem().Tags.ShouldBe(new Dictionary<string, object?>
        {
            ["sys.function.slug"] = "shop-fn",
            ["error.type"] = "System.InvalidOperationException",
        });
        host.Measured("inngest.run.failed").ShouldBeEmpty();
        host.Measured("inngest.run.started").ShouldBeEmpty();
        host.Span("inngest.execution").GetTagItem("inngest.attempt").ShouldBe(1);
    }

    [Fact]
    public async Task A_run_is_counted_from_its_first_call_to_the_return_that_ends_it()
    {
        await using var host = new TelemetryHost();
        var function = TestFunction.Define(async context =>
        {
            await context.Step.RunAsync("charge", () => 42);
            return "done";
        });

        await host.CallAsync(function, Call.New());
        await host.CallAsync(function, Call.New().Data("charge", "42"));

        host.Measured("inngest.run.started").ShouldHaveSingleItem().Tags.ShouldBe(new Dictionary<string, object?> { ["sys.function.slug"] = "shop-fn" });
        host.Measured("inngest.run.completed").ShouldHaveSingleItem().Value.ShouldBe(1);
        host.Measured("inngest.execution.duration").Count.ShouldBe(2);
        host.Measured("inngest.execution.duration").ShouldAllBe(measured => measured.Value >= 0 && !measured.Tags.ContainsKey("error.type"));
        host.Measured("inngest.step.duration").ShouldHaveSingleItem().Tags.ShouldBe(new Dictionary<string, object?>
        {
            ["sys.function.slug"] = "shop-fn",
            ["inngest.step.type"] = "run",
        });
    }

    [Fact]
    public async Task A_function_that_returns_before_a_step_it_started_has_run_completes_once()
    {
        await using var host = new TelemetryHost();
        var function = TestFunction.Define(context =>
        {
            _ = context.Step.RunAsync("audit", () => 1);
            return Task.FromResult<object?>("done");
        });

        (await host.CallAsync(function, Call.New())).ShouldBe(206);
        (await host.CallAsync(function, Call.New(), stepId: Call.Hash("audit"))).ShouldBe(206);
        host.Measured("inngest.run.completed").ShouldBeEmpty();

        (await host.CallAsync(function, Call.New().Data("audit", "1"))).ShouldBe(200);
        host.Measured("inngest.run.completed").ShouldHaveSingleItem();
    }

    // With checkpointing, steps found in a call run in it and the function returns in the same call.
    [Fact]
    public async Task A_checkpointed_run_that_ends_in_its_first_call_is_counted_as_completed()
    {
        var checkpoints = new FakeCheckpointApi();
        await using var host = new TelemetryHost(services => services.AddSingleton<ICheckpointApi>(checkpoints));
        var function = new FunctionDefinition(
            new FunctionOptions { Id = "fn", Triggers = [Trigger.Event("test/event")], Checkpointing = new CheckpointingOptions() },
            _ => new DelegateFunction<JsonElement>(async (context, _) =>
            {
                var price = await context.Step.RunAsync("price", () => 40);
                return await context.Step.RunAsync("charge", () => price + 2);
            }));

        (await host.CallAsync(function, Call.New(), queueItemId: "cWktMQ==")).ShouldBe(200);

        checkpoints.Checkpoints.Count.ShouldBe(2);
        host.Measured("inngest.run.completed").ShouldHaveSingleItem().Value.ShouldBe(1);
        host.Measured("inngest.step.duration").Count.ShouldBe(2);
    }

    [Fact]
    public void Adding_the_instrumentation_twice_registers_one_middleware()
    {
        var services = new ServiceCollection();
        services.AddInngest(options => options.AppId = "shop").AddInstrumentation().AddInstrumentation();

        services.Count(descriptor => descriptor.ServiceType == typeof(InngestMiddleware)).ShouldBe(1);
        services.Count(descriptor => descriptor.ServiceType == typeof(InngestTelemetry)).ShouldBe(1);
    }

    [Theory]
    [InlineData(StepType.Run, "run")]
    [InlineData(StepType.Sleep, "sleep")]
    [InlineData(StepType.WaitForEvent, "wait_for_event")]
    [InlineData(StepType.Invoke, "invoke")]
    [InlineData(StepType.SendEvent, "send_event")]
    [InlineData(StepType.WaitForSignal, "wait_for_signal")]
    [InlineData(StepType.SendSignal, "send_signal")]
    [InlineData(StepType.Fetch, "fetch")]
    [InlineData(StepType.AIInfer, "ai_infer")]
    public void Spans_and_metrics_name_each_step_type(StepType type, string name) =>
        InngestTelemetry.TypeName(type).ShouldBe(name);

    // A step type added later fails here until it has a name, instead of reaching dashboards as its C# name.
    [Fact]
    public void Every_step_type_has_a_snake_case_name_of_its_own()
    {
        var names = Enum.GetValues<StepType>().Select(InngestTelemetry.TypeName).ToArray();

        names.ShouldAllBe(name => Regex.IsMatch(name, "^[a-z]+(_[a-z]+)*$"));
        names.ShouldBeUnique();
    }

    private static Dictionary<string, object?> TagsOf(Activity activity) =>
        activity.TagObjects.ToDictionary(tag => tag.Key, tag => tag.Value, StringComparer.Ordinal);
}
