using Inngest.IntegrationTests.Evaluation.Harness;

namespace Inngest.IntegrationTests.Steps;

// A dev server and an app with the functions of the step tool tests: races in race mode, named steps and scoped steps.
public sealed class StepsFixture : EvaluationFixture
{
    protected override void AddEvaluationFunctions(IInngestBuilder inngest)
    {
        RaceFunctions.Add(inngest);
        NamedStepFunctions.Add(inngest);
        ScopedStepFunctions.Add(inngest);
    }
}
