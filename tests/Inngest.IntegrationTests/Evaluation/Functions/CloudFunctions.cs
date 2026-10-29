namespace Inngest.IntegrationTests.Evaluation.Functions;

internal sealed record CloudFlowResult(int First, string? Resumed, int Child, string Last);

// Functions of the apps that run in cloud mode against `inngest start`, where every call is signed.
internal static class CloudFunctions
{
    public const string Flow = "eval/cloud.flow";
    public const string Resume = "eval/cloud.resume";
    public const string BigState = "eval/cloud.big-state";

    public static void Add(IInngestBuilder inngest)
    {
        // Every kind of call: a step, a sleep, a wait, an invoke and the call that returns.
        inngest.AddFunction<ValueEvent>(
            new FunctionOptions { Id = "cloud-flow", Triggers = [Trigger.Event(Flow)] },
            static async (context, _) =>
            {
                var first = await context.Step.RunAsync("first", () => context.Event.Data.Value + 1);
                await context.Step.SleepAsync("pause", TimeSpan.FromSeconds(1));
                var resumed = await context.Step.WaitForEventAsync<Resume>("resume", new WaitForEventOptions(Resume, TimeSpan.FromSeconds(30)) { Match = "data.testId" });
                var child = await context.Step.InvokeAsync<int>("triple", new InvokeOptions("cloud-child") { Data = new ValueEvent(context.Event.Data.TestId, first) });
                var last = await context.Step.RunAsync("last", () => $"{first}/{resumed?.Data.Message}/{child}");
                return new CloudFlowResult(first, resumed?.Data.Message, child, last);
            });

        inngest.AddFunction<ValueEvent>(
            new FunctionOptions { Id = "cloud-child", Triggers = [] },
            static (context, _) => Task.FromResult<object?>(context.Event.Data.Value * 3));

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "cloud-big-state", Triggers = [Trigger.Event(BigState)] },
            static async (context, _) =>
            {
                var testId = context.Event.Data.TestId;
                var chunks = new List<string>();
                for (var i = 1; i <= 3; i++)
                {
                    var index = i;
                    chunks.Add(await context.Step.RunAsync($"chunk-{index}", () => StepDataFunctions.LargeText($"{testId}-{index}", LargeStateFunctions.ChunkLength)));
                }

                return await context.Step.RunAsync("check", () =>
                    new BigStateResult([.. chunks.Select(chunk => chunk.Length)], [.. chunks.Select(StepDataFunctions.Sha256)]));
            });
    }
}

internal static class RotationFunctions
{
    public const string Steps = "eval/rot.steps";
    public const string HelloId = "rot-hello";

    public static void Add(IInngestBuilder inngest)
    {
        inngest.AddFunction<ValueEvent>(
            new FunctionOptions { Id = "rot-steps", Triggers = [Trigger.Event(Steps)] },
            static async (context, _) =>
            {
                var first = await context.Step.RunAsync("first", () => context.Event.Data.Value * 2);
                await context.Step.SleepAsync("pause", TimeSpan.FromSeconds(1));
                return await context.Step.RunAsync("second", () => first + 1);
            });

        // No steps, so a direct call gets the output in one response.
        inngest.AddFunction<TextEvent>(
            new FunctionOptions { Id = HelloId, Triggers = [Trigger.Event("eval/rot.hello")] },
            static (context, _) => Task.FromResult<object?>($"Hello, {context.Event.Data.Text}!"));
    }
}
