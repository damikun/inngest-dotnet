using Inngest.IntegrationTests.Evaluation.Harness;

namespace Inngest.IntegrationTests.AI;

// A dev server and an app whose functions call a scripted model through the durable chat client.
public sealed class ChatFixture : EvaluationFixture
{
    protected override void AddEvaluationFunctions(IInngestBuilder inngest) => ChatFunctions.Add(inngest);
}
