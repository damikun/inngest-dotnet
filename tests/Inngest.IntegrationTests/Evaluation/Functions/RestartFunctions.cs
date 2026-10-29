using Inngest.IntegrationTests.Evaluation.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.Evaluation.Functions;

// Functions of the app that scenarios restart. Each records which app instance ran its steps.
internal static class RestartFunctions
{
    public const string Restart = "eval/dur.restart";
    public const string Outage = "eval/dur.outage";
    public const string Interrupted = "eval/dur.interrupted";

    public static readonly TimeSpan RestartNap = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan OutageNap = TimeSpan.FromSeconds(1);

    public static void Add(IInngestBuilder inngest)
    {
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "dur-restart", Triggers = [Trigger.Event(Restart)] },
            static (context, _) => SleepBetweenStepsAsync(context, RestartNap));

        // Plenty of retries, since the server keeps calling while the app is down.
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "dur-outage", Retries = 10, Triggers = [Trigger.Event(Outage)] },
            static (context, _) => SleepBetweenStepsAsync(context, OutageNap));

        // The first execution of the step never finishes on its own: it runs until the app shuts down under it.
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "dur-interrupted", Retries = 10, Triggers = [Trigger.Event(Interrupted)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                var instance = context.Services.GetRequiredService<AppInstance>().Id;
                return await context.Step.RunAsync("work", async cancellationToken =>
                {
                    if (observed.Record("started", instance) == 1)
                    {
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                    }

                    return $"finished on {instance}";
                });
            });
    }

    private static async Task<object?> SleepBetweenStepsAsync(FunctionContext<RunEvent> context, TimeSpan nap)
    {
        var observed = context.Observed();
        var instance = context.Services.GetRequiredService<AppInstance>().Id;
        await context.Step.RunAsync("before", () => observed.Record("before", instance));
        await context.Step.SleepAsync("nap", nap);
        await context.Step.RunAsync("after", () => observed.Record("after", instance));
        return instance;
    }
}
