using System.Text.Json;
using Inngest.Execution;
using Inngest.Tests.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Tests.Steps;

// Display names: what the Inngest UI shows for a step, while the id stays what Inngest memoizes the step by.
public sealed class StepNameTests : IDisposable
{
    private static readonly StepOptions Charge = new("charge") { Name = "Charge the card" };

    private readonly List<string?> _names = [];
    private readonly EngineHost _host;

    public StepNameTests()
    {
        _host = new EngineHost(services => services.AddSingleton<InngestMiddleware>(new NameRecorder(_names)));
    }

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task A_planned_run_step_shows_its_name_and_keeps_its_id()
    {
        var function = TestFunction.Define(async context => await context.Step.RunAsync(Charge, () => "charged"));

        var result = await _host.CallAsync(function, Call.New().DisableImmediateExecution());

        result.Body.ShouldBe(
            $$$"""[{"id":"{{{Call.Hash("charge")}}}","op":"StepPlanned","name":"charge","displayName":"Charge the card","opts":{"type":"step.run"},"userland":{"id":"charge"}}]""");
    }

    [Fact]
    public async Task A_run_step_that_runs_reports_its_name()
    {
        var function = TestFunction.Define(async context => await context.Step.RunAsync(Charge, () => "charged"));

        var result = await _host.CallAsync(function, Call.New());

        result.Body.ShouldBe(
            $$$"""[{"id":"{{{Call.Hash("charge")}}}","op":"StepRun","name":"charge","displayName":"Charge the card","opts":{"type":"step.run"},"userland":{"id":"charge"},"data":"charged","timing":{"a":{{{EngineHost.NowNanoseconds}}},"b":0}}]""");
    }

    [Theory]
    [InlineData(0, "StepError")]
    [InlineData(2, "StepFailed")]
    public async Task A_failing_step_reports_its_name(int attempt, string op)
    {
        Func<string> decline = () => throw new InvalidOperationException("card declined");
        var function = TestFunction.Define(async context => await context.Step.RunAsync(Charge, decline));

        var result = await _host.CallAsync(function, Call.New().Attempt(attempt, maxAttempts: 3));

        var reported = result.Op();
        reported.GetProperty("op").GetString().ShouldBe(op);
        reported.GetProperty("name").GetString().ShouldBe("charge");
        reported.GetProperty("displayName").GetString().ShouldBe("Charge the card");
        reported.GetProperty("userland").GetRawText().ShouldBe("""{"id":"charge"}""");
        reported.GetProperty("error").GetProperty("message").GetString().ShouldBe("card declined");
    }

    [Fact]
    public async Task Every_kind_of_step_reports_its_name()
    {
        var function = TestFunction.Define(async context =>
        {
            await Task.WhenAll(
                context.Step.SleepAsync(new StepOptions("cool-off") { Name = "Cool off" }, TimeSpan.FromMinutes(90)),
                context.Step.SleepUntilAsync(new StepOptions("until") { Name = "Until Friday" }, new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero)),
                context.Step.WaitForEventAsync<JsonElement>(
                    new StepOptions("approval") { Name = "Wait for approval" },
                    new WaitForEventOptions("app/approved", TimeSpan.FromDays(1))),
                context.Step.InvokeAsync<int>(new StepOptions("invoke-billing") { Name = "Bill the customer" }, new InvokeOptions("billing")),
                context.Step.SendEventAsync(
                    new StepOptions("notify") { Name = "Notify shipping" },
                    [new InngestEvent("app/shipped", new { orderId = 1 })]));
            return null;
        });

        var result = await _host.CallAsync(function, Call.New());

        result.Body.ShouldBe(
            "[" +
            $$$"""{"id":"{{{Call.Hash("cool-off")}}}","op":"Sleep","name":"1h30m","displayName":"Cool off","opts":{"duration":"1h30m"},"userland":{"id":"cool-off"}},""" +
            $$$"""{"id":"{{{Call.Hash("until")}}}","op":"Sleep","name":"2026-10-30T08:00:00.000Z","displayName":"Until Friday","opts":{"duration":"2026-10-30T08:00:00.000Z"},"userland":{"id":"until"}},""" +
            $$$"""{"id":"{{{Call.Hash("approval")}}}","op":"WaitForEvent","name":"app/approved","displayName":"Wait for approval","opts":{"event":"app/approved","timeout":"1d"},"userland":{"id":"approval"}},""" +
            $$$"""{"id":"{{{Call.Hash("invoke-billing")}}}","op":"InvokeFunction","displayName":"Bill the customer","opts":{"function_id":"test-app-billing","payload":{}},"userland":{"id":"invoke-billing"}},""" +
            $$$"""{"id":"{{{Call.Hash("notify")}}}","op":"StepPlanned","name":"sendEvent","displayName":"Notify shipping","opts":{"type":"step.sendEvent"},"userland":{"id":"notify"}}""" +
            "]");
    }

    [Fact]
    public async Task The_run_overloads_for_other_handler_shapes_take_a_name_too()
    {
        var function = TestFunction.Define(async context =>
        {
            await Task.WhenAll(
                context.Step.RunAsync(new StepOptions("value") { Name = "A value" }, () => 1),
                context.Step.RunAsync(new StepOptions("action") { Name = "An action" }, () => { }),
                context.Step.RunAsync(new StepOptions("task") { Name = "A task" }, () => Task.CompletedTask),
                context.Step.RunAsync(new StepOptions("task-of") { Name = "A task of a value" }, () => Task.FromResult(1)),
                context.Step.RunAsync(new StepOptions("token") { Name = "With a token" }, (CancellationToken _) => Task.CompletedTask),
                context.Step.WaitForEventAsync(new StepOptions("raw") { Name = "Any event" }, new WaitForEventOptions("app/any", TimeSpan.FromHours(1))),
                context.Step.SendEventAsync(new StepOptions("one") { Name = "One event" }, new InngestEvent("app/one", new { })));
            return null;
        });

        var result = await _host.CallAsync(function, Call.New());

        result.DisplayNames.ShouldBe(["A value", "An action", "A task", "A task of a value", "With a token", "Any event", "One event"]);
        result.Ops.Select(op => op.GetProperty("userland").GetProperty("id").GetString())
            .ShouldBe(["value", "action", "task", "task-of", "token", "raw", "one"]);
    }

    [Fact]
    public async Task A_name_does_not_change_the_hashed_id_so_memoized_results_still_match()
    {
        var function = TestFunction.Define(async context => await context.Step.RunAsync(Charge, () => "charged again"));

        var result = await _host.CallAsync(function, Call.New().Data("charge", "\"charged\""));

        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe("\"charged\"");
    }

    [Fact]
    public async Task A_named_step_used_in_a_loop_keeps_its_name_on_every_use()
    {
        var function = TestFunction.Define(async context =>
        {
            await Task.WhenAll(Enumerable.Range(0, 3).Select(i => context.Step.RunAsync(new StepOptions("item") { Name = "Process item" }, () => i)));
            return null;
        });

        var result = await _host.CallAsync(function, Call.New());

        result.DisplayNames.ShouldBe(["Process item", "Process item", "Process item"]);
        result.Ops.Select(op => op.GetProperty("userland").GetRawText())
            .ShouldBe(["""{"id":"item"}""", """{"id":"item","index":1}""", """{"id":"item","index":2}"""]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Without_a_name_the_id_is_shown(string? name)
    {
        var function = TestFunction.Define(async context => await context.Step.RunAsync(new StepOptions("charge") { Name = name }, () => "charged"));

        var result = await _host.CallAsync(function, Call.New().DisableImmediateExecution());

        result.DisplayNames.ShouldBe(["charge"]);
    }

    [Fact]
    public async Task A_string_converts_to_step_options_with_the_id_alone()
    {
        StepOptions options = "charge";

        options.ShouldBe(new StepOptions("charge"));
        options.Name.ShouldBeNull();
        options.ParallelMode.ShouldBeNull();

        var function = TestFunction.Define(async context => await Named(context.Step, "charge"));
        var result = await _host.CallAsync(function, Call.New().DisableImmediateExecution());
        result.DisplayNames.ShouldBe(["charge"]);

        static Task<string> Named(IStepTools step, StepOptions options) => step.RunAsync(options, () => "charged");
    }

    [Fact]
    public async Task Missing_options_and_empty_ids_throw_at_the_call_site()
    {
        var errors = new List<string>();
        var function = TestFunction.Define(context =>
        {
            void Expect(Action call)
            {
                try
                {
                    call();
                }
                catch (ArgumentException exception)
                {
                    errors.Add(exception.ParamName ?? "?");
                }
            }

            StepOptions missing = null!;
            Expect(() => context.Step.RunAsync(missing, _ => Task.FromResult(1)));
            Expect(() => context.Step.RunAsync(missing, () => 1));
            Expect(() => context.Step.SleepAsync(missing, TimeSpan.Zero));
            Expect(() => context.Step.SleepUntilAsync(missing, DateTimeOffset.UnixEpoch));
            Expect(() => context.Step.WaitForEventAsync(missing, new WaitForEventOptions("app/approved", TimeSpan.FromMinutes(1))));
            Expect(() => context.Step.InvokeAsync<int>(missing, new InvokeOptions("billing")));
            Expect(() => context.Step.SendEventAsync(missing, new InngestEvent("app/shipped", new { })));
            Expect(() => context.Step.RunAsync(new StepOptions(""), () => 1));
            return Task.FromResult<object?>(null);
        });

        await _host.CallAsync(function, Call.New());

        errors.ShouldBe(["stepOptions", "stepOptions", "stepOptions", "stepOptions", "stepOptions", "stepOptions", "stepOptions", "id"]);
    }

    // The same diagnostics the TypeScript SDK sends: the display name, and the hashed id in brackets.
    [Fact]
    public async Task The_step_not_found_report_shows_names()
    {
        var function = TestFunction.Define(async context =>
        {
            await Task.WhenAll(
                context.Step.RunAsync(Charge, () => "charged"),
                context.Step.RunAsync("ship", () => "shipped"));
            return null;
        });
        var missing = Call.Hash("missing-step");

        var pending = _host.Start(function, Call.New().DisableImmediateExecution(), stepId: missing);
        _host.Time.Advance(Invocation.StepNotFoundTimeout);
        using var result = await pending;

        var charge = Call.Hash("charge");
        var ship = Call.Hash("ship");
        var body = JsonDocument.Parse(result.Body.WrittenMemory).RootElement;
        body.GetProperty("error").GetString().ShouldBe(
            $"Could not find step \"{missing}\" to run; timed out. Found new steps: \nCharge the card ({charge})\nship ({ship}).");
        body.GetProperty("foundSteps").GetRawText().ShouldBe(
            $$$"""[{"id":"{{{charge}}}","name":"charge","displayName":"Charge the card"},{"id":"{{{ship}}}","name":"ship","displayName":"ship"}]""");
    }

    [Fact]
    public async Task Middleware_sees_the_name_of_new_and_memoized_steps()
    {
        var function = TestFunction.Define(async context =>
        {
            await context.Step.RunAsync(Charge, () => "charged");
            return await context.Step.RunAsync("ship", () => "shipped");
        });

        await _host.CallAsync(function, Call.New().Data("charge", "\"charged\""));

        _names.ShouldBe(["WrapStep charge: Charge the card", "WrapStep ship: ", "OnStepStart ship: "]);
    }

    [Fact]
    public async Task Middleware_sees_the_name_of_a_step_that_runs()
    {
        var function = TestFunction.Define(async context => await context.Step.RunAsync(Charge, () => "charged"));

        await _host.CallAsync(function, Call.New());

        _names.ShouldBe(["WrapStep charge: Charge the card", "OnStepStart charge: Charge the card"]);
    }

    private sealed class NameRecorder(List<string?> names) : InngestMiddleware
    {
        public override ValueTask OnStepStartAsync(FunctionContext context, StepInfo step)
        {
            names.Add($"OnStepStart {step.Id}: {step.Name}");
            return ValueTask.CompletedTask;
        }

        public override Task<object?> WrapStepAsync(FunctionContext context, StepInfo step, Func<Task<object?>> next)
        {
            names.Add($"WrapStep {step.Id}: {step.Name}");
            return next();
        }
    }
}
