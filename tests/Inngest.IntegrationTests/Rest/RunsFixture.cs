using Inngest.IntegrationTests.Evaluation.Harness;

namespace Inngest.IntegrationTests.Rest;

// A dev server and an app with the functions of the runs client tests. The client under test is the app's own
// IInngestRunsClient, pointed at the dev server like the rest of the SDK.
public sealed class RunsFixture : EvaluationFixture
{
    protected override void AddEvaluationFunctions(IInngestBuilder inngest) => RunsFunctions.Add(inngest);
}
