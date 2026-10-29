using Inngest.IntegrationTests.Evaluation.Harness;
using Inngest.IntegrationTests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.Evaluation.Functions;

internal sealed record ConnectFlowResult(int Start, IReadOnlyList<int> Parts, string? Resumed, int Total);

internal sealed record ConnectParentResult(int Child, IReadOnlyList<string> Sent);

// Functions of the Connect worker app. Their events start with eval/con so that no HTTP app sees them.
internal static class ConnectWorkerFunctions
{
    public const string Flow = "eval/con.flow";
    public const string Resume = "eval/con.resume";
    public const string Parent = "eval/con.parent";
    public const string Received = "eval/con.received";
    public const string Many = "eval/con.many";
    public const string Failing = "eval/con.failing";
    public const string Restart = "eval/con.restart";
    public const string BigState = "eval/con.big-state";
    public const string CloudSteps = "eval/con.cloud-steps";

    public static void Add(IInngestBuilder inngest)
    {
        inngest.AddFunction<ValueEvent>(
            new FunctionOptions { Id = "con-flow", Triggers = [Trigger.Event(Flow)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                var start = await context.Step.RunAsync("start", () =>
                {
                    observed.Record("start");
                    return context.Event.Data.Value + 1;
                });
                var parts = await Task.WhenAll(Enumerable.Range(1, 3).Select(index => context.Step.RunAsync($"part-{index}", () =>
                {
                    observed.Record("part", index);
                    return index * start;
                })));
                await context.Step.SleepAsync("pause", TimeSpan.FromSeconds(1));
                var resumed = await context.Step.WaitForEventAsync<Resume>("resume", new WaitForEventOptions(Resume, TimeSpan.FromSeconds(30)) { Match = "data.testId" });
                var total = await context.Step.RunAsync("total", () => parts.Sum());
                return new ConnectFlowResult(start, parts, resumed?.Data.Message, total);
            });

        inngest.AddFunction<ValueEvent>(
            new FunctionOptions { Id = "con-parent", Triggers = [Trigger.Event(Parent)] },
            static async (context, _) =>
            {
                var child = await context.Step.InvokeAsync<int>("child", new InvokeOptions("con-child") { Data = context.Event.Data });
                var sent = await context.Step.SendEventAsync("notify", new InngestEvent(Received, new ValueEvent(context.Event.Data.TestId, child)));
                return new ConnectParentResult(child, sent.Ids);
            });

        inngest.AddFunction<ValueEvent>(
            new FunctionOptions { Id = "con-child", Triggers = [] },
            static (context, _) => Task.FromResult<object?>(context.Event.Data.Value * 7));

        inngest.AddFunction<ValueEvent>(
            new FunctionOptions { Id = "con-receiver", Triggers = [Trigger.Event(Received)] },
            static (context, _) =>
            {
                context.Observed().Record("received", context.Event.Data.Value);
                return Task.FromResult<object?>(context.Event.Data.Value);
            });

        inngest.AddFunction<ValueEvent>(
            new FunctionOptions { Id = "con-many", Triggers = [Trigger.Event(Many)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                var value = context.Event.Data.Value;
                var doubled = await context.Step.RunAsync("double", () => value * 2);
                return await context.Step.RunAsync("finish", () =>
                {
                    observed.Record("done", value);
                    return doubled + 1;
                });
            });

        // Past 4 MB the gateway's request says use_api, and the worker loads the state over HTTP from the API.
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "con-big-state", Triggers = [Trigger.Event(BigState)] },
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

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "con-failing", Retries = 1, Triggers = [Trigger.Event(Failing)] },
            static (context, _) =>
            {
                context.Observed().Record("attempt", context.Attempt);
                throw new InvalidOperationException("fails over the WebSocket too");
            },
            onFailure: static (context, _) =>
            {
                var testId = context.Event.Data.Event.GetProperty("data").GetProperty("testId").GetString()!;
                context.Services.GetRequiredService<Probe>().For(testId).Record("on-failure", context.Event.Data.Error.Message);
                return Task.CompletedTask;
            });
    }

    // For the worker in cloud mode.
    public static void AddCloud(IInngestBuilder inngest) =>
        inngest.AddFunction<ValueEvent>(
            new FunctionOptions { Id = "con-cloud-steps", Triggers = [Trigger.Event(CloudSteps)] },
            static async (context, _) =>
            {
                var first = await context.Step.RunAsync("first", () => context.Event.Data.Value + 1);
                await context.Step.SleepAsync("pause", TimeSpan.FromSeconds(1));
                return await context.Step.RunAsync("second", () => first * 2);
            });

    // For the worker that a scenario restarts. Each worker has an AppInstance of its own.
    public static void AddRestart(IInngestBuilder inngest) =>
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "con-restart", Triggers = [Trigger.Event(Restart)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                var instance = context.Services.GetRequiredService<AppInstance>().Id;
                await context.Step.RunAsync("before", () => observed.Record("before", instance));
                await context.Step.SleepAsync("nap", TimeSpan.FromSeconds(3));
                await context.Step.RunAsync("after", () => observed.Record("after", instance));
                return instance;
            });
}
