using Inngest.IntegrationTests.Evaluation.Harness;

namespace Inngest.IntegrationTests.Core;

// A dev server and an app with the functions of the core gap work: cleanup functions for system events, a cron
// trigger with jitter, step failures with causes and payload transforms. The function ids are short, so the composite
// ids the system events carry read "core-<id>".
public sealed class CoreFixture : EvaluationFixture
{
    public override string AppId => CoreFunctions.AppId;

    protected override void AddEvaluationFunctions(IInngestBuilder inngest) => CoreFunctions.Add(inngest);
}
