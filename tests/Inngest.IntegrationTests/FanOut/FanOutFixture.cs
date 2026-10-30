using Inngest.IntegrationTests.Evaluation.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.FanOut;

// A dev server and an app with the functions of the fan-out tests: windows of parallel items, failures, races and chunks
// that run as child runs.
public sealed class FanOutFixture : EvaluationFixture
{
    protected override void AddEvaluationFunctions(IInngestBuilder inngest)
    {
        inngest.Services.AddSingleton<InFlight>();
        FanOutFunctions.Add(inngest);
    }
}
