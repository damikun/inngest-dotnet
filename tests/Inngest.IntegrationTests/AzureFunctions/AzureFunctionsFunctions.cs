using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Functions;
using Inngest.IntegrationTests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.AzureFunctions;

internal static class AzureFunctionsFunctions
{
    public const string AppId = "azfn";
    public const string Greet = "azfn/greet";
    public const string Slow = "azfn/slow";

    // Longer than the three seconds after which a streamed call starts writing to the connection.
    public static readonly TimeSpan SlowStep = TimeSpan.FromSeconds(5);

    // Two steps, each of which records that it ran, so that a test sees how often.
    public static void AddGreeting(IInngestBuilder inngest) =>
        inngest.AddFunction<GreetEvent>(
            new FunctionOptions { Id = "greet", Triggers = [Trigger.Event(Greet)] },
            static async (context, _) =>
            {
                var observed = context.Services.GetRequiredService<Probe>().For(context.Event.Data.TestId);
                var greeting = await context.Step.RunAsync("compose", () =>
                {
                    observed.Record("compose");
                    return new Greeting($"Hello, {context.Event.Data.Name}!");
                });

                return await context.Step.RunAsync("shout", () =>
                {
                    observed.Record("shout");
                    return new TextResult(greeting.Message.ToUpperInvariant());
                });
            });

    public static void AddSlow(IInngestBuilder inngest) =>
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "slow", Triggers = [Trigger.Event(Slow)] },
            static async (context, _) => await context.Step.RunAsync("work", async cancellationToken =>
            {
                await Task.Delay(SlowStep, cancellationToken);
                return new TextResult("finished after a long step");
            }));
}
