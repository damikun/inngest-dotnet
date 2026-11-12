using Inngest.IntegrationTests.Evaluation.Functions;

namespace Inngest.IntegrationTests.Rest;

// Functions whose runs the tests read and cancel through IInngestRunsClient, and manage through IInngestManagementClient.
internal static class RunsFunctions
{
    public const string Greet = "itests/rest.greet";
    public const string FanOut = "itests/rest.fanout";
    public const string Nap = "itests/rest.nap";
    public const string Pausable = "itests/rest.pausable";

    // The ids the management tests name the functions by, without the app id.
    public const string GreetId = "rest-greet";
    public const string PausableId = "rest-pausable";

    public static void Add(IInngestBuilder inngest)
    {
        // A step first, so the output comes from a later call that replays it.
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = GreetId, Triggers = [Trigger.Event(Greet)] },
            static async (context, _) =>
            {
                var name = await context.Step.RunAsync("name", static () => "dalibor");
                return new Greeting($"hi {name}", context.Event.Data.TestId);
            });

        // Two functions on one event, so that the event has two runs.
        foreach (var branch in new[] { "a", "b" })
        {
            inngest.AddFunction<RunEvent>(
                new FunctionOptions { Id = $"rest-fanout-{branch}", Triggers = [Trigger.Event(FanOut)] },
                (context, _) => Task.FromResult<object?>(new Branch(branch, context.Event.Data.TestId)));
        }

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "rest-nap", Triggers = [Trigger.Event(Nap)] },
            static async (context, _) =>
            {
                await context.Step.SleepAsync("nap", TimeSpan.FromHours(1));
                return "woke up";
            });

        // Only the management tests use it, as they pause it.
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = PausableId, Triggers = [Trigger.Event(Pausable)] },
            static (context, _) => Task.FromResult<object?>(new Greeting("ran", context.Event.Data.TestId)));
    }
}

internal sealed record Greeting(string Text, string TestId);

internal sealed record Branch(string Name, string TestId);
