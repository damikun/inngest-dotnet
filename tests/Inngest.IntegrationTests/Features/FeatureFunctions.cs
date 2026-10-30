using Inngest.IntegrationTests.Evaluation.Functions;

namespace Inngest.IntegrationTests.Features;

internal sealed record Prompt(string Text);

internal sealed record Progress(string Text, int Percent);

internal static class FeatureFunctions
{
    public const string LongStep = "features/stream.long-step";
    public const string Wrapped = "features/ai.wrap";
    public const string Published = "features/realtime.publish";
    public const string SessionsParent = "features/sessions.parent";
    public const string SessionsChild = "features/sessions.child";
    public const string Streamed = "features/realtime.stream";
    public const string TypedPublish = "features/realtime.typed";

    public static RealtimeTopic Tokens(string testId) => new($"run:{testId}", "tokens");

    public static RealtimeTopic<Progress> Status(string testId) => new($"run:{testId}", "status");

    // Longer than the three seconds after which a streamed call starts writing to the connection.
    public static readonly TimeSpan SlowStep = TimeSpan.FromSeconds(7);

    public static void Add(IInngestBuilder inngest)
    {
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "stream-long-step", Triggers = [Trigger.Event(LongStep)] },
            static async (context, _) => await context.Step.RunAsync("slow", async cancellationToken =>
            {
                await Task.Delay(SlowStep, cancellationToken);
                return "finished after a long step";
            }));

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "ai-wrap", Triggers = [Trigger.Event(Wrapped)] },
            static async (context, _) => await context.Step.AIWrapAsync("draft", new Prompt("haiku"), prompt => Task.FromResult($"A {prompt.Text}")));

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "realtime-publish", Triggers = [Trigger.Event(Published)] },
            static async (context, _) =>
            {
                var topic = new RealtimeTopic($"run:{context.Event.Data.TestId}", "status");
                var progress = await context.Step.PublishAsync("status", topic, new Progress("Analyzing", 10));
                return progress.Text;
            });

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "realtime-stream", Triggers = [Trigger.Event(Streamed)] },
            static async (context, _) => await context.Step.PublishStreamAsync("reply", Tokens(context.Event.Data.TestId), Words()));

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "realtime-typed", Triggers = [Trigger.Event(TypedPublish)] },
            static async (context, _) => (await context.Step.PublishAsync("status", Status(context.Event.Data.TestId), new Progress("Packed", 50))).Text);

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "sessions-parent", Triggers = [Trigger.Event(SessionsParent)] },
            static async (context, _) =>
            {
                context.Observed().Record("parent", Describe(context.Sessions));
                await context.Step.SendEventAsync("child", new InngestEvent(SessionsChild, new RunEvent(context.Event.Data.TestId)));
                return null;
            });

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "sessions-child", Triggers = [Trigger.Event(SessionsChild)] },
            static (context, _) =>
            {
                context.Observed().Record("child", Describe(context.Sessions));
                return Task.FromResult<object?>(null);
            });
    }

    // Words of a model reply as they would arrive, a little apart.
    private static async IAsyncEnumerable<string> Words()
    {
        foreach (var word in new[] { "Your ", "order ", "is ", "on ", "its ", "way ", "😀" })
        {
            await Task.Delay(20);
            yield return word;
        }
    }

    private static string Describe(IReadOnlyDictionary<string, string> sessions) =>
        string.Join(",", sessions.Select(session => $"{session.Key}={session.Value}"));
}
