using Inngest.IntegrationTests.Evaluation.Harness;

namespace Inngest.IntegrationTests.Webhooks;

// A dev server and an app with the functions that webhook events start.
public sealed class WebhooksFixture : EvaluationFixture
{
    protected override void AddEvaluationFunctions(IInngestBuilder inngest) => WebhookFunctions.Add(inngest);
}
