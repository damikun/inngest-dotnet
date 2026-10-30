using Inngest.Functions;
using Inngest.Tests.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Tests.Steps;

public sealed class AIWrapStepTests : IDisposable
{
    private readonly List<string> _prompts = [];
    private readonly EngineHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task AI_wrap_runs_the_handler_and_reports_its_input_like_inngest_js()
    {
        var result = await _host.CallAsync(Drafting(), Call.New());

        result.OpCodes.ShouldBe(["StepRun"]);
        var op = result.Op();
        op.GetProperty("opts").GetRawText().ShouldBe("""{"input":[{"text":"Write a haiku"}],"type":"step.ai.wrap"}""");
        op.GetProperty("data").GetRawText().ShouldBe("""{"text":"Draft for Write a haiku"}""");
        _prompts.ShouldBe(["Write a haiku"]);
    }

    [Fact]
    public async Task The_memoized_result_is_handed_back_without_calling_the_model_again()
    {
        var result = await _host.CallAsync(Drafting(), Call.New().Data("draft", """{"text":"Remembered"}"""));

        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe("\"Remembered\"");
        _prompts.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_input_edited_in_the_Inngest_UI_reaches_the_handler_and_the_op()
    {
        var result = await _host.CallAsync(Drafting(), Call.New().Input("draft", """[{"text":"Write a limerick"}]"""));

        result.OpCodes.ShouldBe(["StepRun"]);
        result.Op().GetProperty("opts").GetProperty("input").GetRawText().ShouldBe("""[{"text":"Write a limerick"}]""");
        _prompts.ShouldBe(["Write a limerick"]);
    }

    [Fact]
    public async Task A_step_options_overload_names_the_step_and_planned_steps_carry_the_input()
    {
        var function = TestFunction.Define(async context =>
        {
            await Task.WhenAll(
                context.Step.AIWrapAsync(new StepOptions("a") { Name = "First draft" }, new Prompt("one"), Draft),
                context.Step.AIWrapAsync(new StepOptions("b") { Name = "Second draft" }, new Prompt("two"), Draft));
            return null;
        });

        var result = await _host.CallAsync(function, Call.New());

        result.OpCodes.ShouldBe(["StepPlanned", "StepPlanned"]);
        result.DisplayNames.ShouldBe(["First draft", "Second draft"]);
        result.Ops.Select(op => op.GetProperty("opts").GetProperty("input")[0].GetProperty("text").GetString()).ShouldBe(["one", "two"]);
        _prompts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Middleware_sees_an_AI_wrap_step()
    {
        var types = new List<StepType>();
        using var host = new EngineHost(services => services.AddSingleton<InngestMiddleware>(new TypeRecorder(types)));

        var result = await host.CallAsync(Drafting(), Call.New());

        result.OpCodes.ShouldBe(["StepRun"]);
        types.ShouldBe([StepType.AIWrap]);
    }

    private FunctionDefinition Drafting() => TestFunction.Define(async context =>
    {
        var reply = await context.Step.AIWrapAsync("draft", new Prompt("Write a haiku"), Draft);
        return reply.Text;
    });

    private Task<Reply> Draft(Prompt prompt)
    {
        _prompts.Add(prompt.Text);
        return Task.FromResult(new Reply($"Draft for {prompt.Text}"));
    }

    private sealed record Prompt(string Text);

    private sealed record Reply(string Text);

    private sealed class TypeRecorder(List<StepType> types) : InngestMiddleware
    {
        public override Task<object?> WrapStepHandlerAsync(FunctionContext context, StepInfo step, Func<Task<object?>> next)
        {
            types.Add(step.Type);
            return next();
        }
    }
}
