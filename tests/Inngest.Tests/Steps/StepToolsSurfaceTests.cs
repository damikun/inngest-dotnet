using System.Text.Json;
using Inngest.Tests.Client;
using Inngest.Tests.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Tests.Steps;

public sealed class StepToolsSurfaceTests
{
    [Fact]
    public async Task Middleware_sees_the_type_of_each_new_step()
    {
        var types = new List<StepType>();
        using var host = new EngineHost(services => services.AddSingleton<InngestMiddleware>(new TypeRecorder(types)));
        var function = TestFunction.Define(async context =>
        {
            await context.Step.WaitForSignalAsync("signal", new WaitForSignalOptions("approval/order-1", TimeSpan.FromHours(1)));
            await context.Step.SendSignalAsync("send", new InngestSignal("approval/order-1"));
            using var response = await context.Step.FetchAsync("fetch", new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/"));
            await context.Step.InferAsync("infer", new InferOptions(AIFormat.Anthropic, new Uri("https://api.anthropic.com/v1/messages"), new { model = "claude" }));
            return null;
        });

        var result = await host.CallAsync(
            function,
            Call.New().Memo("signal", "null").Data("send", """{"runId":null}""").Data("fetch", """{"status_code":204}""").Data("infer", "{}"));

        result.StatusCode.ShouldBe(200);
        types.ShouldBe([StepType.WaitForSignal, StepType.SendSignal, StepType.Fetch, StepType.AIInfer]);
    }

    // Users fake IStepTools; the new tools have defaults so that their fakes keep compiling.
    [Fact]
    public void A_fake_that_predates_the_new_tools_refuses_them_by_name()
    {
        IStepTools step = new UnusedStepTools();

        Should.Throw<NotSupportedException>(() => step.WaitForSignalAsync<JsonElement>("s", new WaitForSignalOptions("a", TimeSpan.FromMinutes(1))))
            .Message.ShouldBe("UnusedStepTools does not implement WaitForSignalAsync.");
        Should.Throw<NotSupportedException>(() => step.SendSignalAsync("s", new InngestSignal("a")))
            .Message.ShouldBe("UnusedStepTools does not implement SendSignalAsync.");
        Should.Throw<NotSupportedException>(() => step.FetchAsync("s", new HttpRequestMessage(HttpMethod.Get, "https://example.com/")))
            .Message.ShouldBe("UnusedStepTools does not implement FetchAsync.");
        Should.Throw<NotSupportedException>(() => step.InferAsync<JsonElement>("s", new InferOptions(AIFormat.OpenAIChat, new Uri("https://example.com/"), new { })))
            .Message.ShouldBe("UnusedStepTools does not implement InferAsync.");
        Should.Throw<NotSupportedException>(() => step.AIWrapAsync<string, string>("s", "prompt", (prompt, _) => Task.FromResult(prompt)))
            .Message.ShouldBe("UnusedStepTools does not implement AIWrapAsync.");
        Should.Throw<NotSupportedException>(() => step.PublishAsync("s", new RealtimeTopic("chat", "tokens"), "Hel"))
            .Message.ShouldBe("UnusedStepTools does not implement PublishAsync.");
    }

    private sealed class TypeRecorder(List<StepType> types) : InngestMiddleware
    {
        public override Task<object?> WrapStepAsync(FunctionContext context, StepInfo step, Func<Task<object?>> next)
        {
            types.Add(step.Type);
            return next();
        }
    }
}
