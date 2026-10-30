using Inngest.IntegrationTests.Evaluation.Harness;

namespace Inngest.IntegrationTests.Features;

// A dev server and an app that streams long calls, with the functions of the newer SDK features: AI wrap steps,
// realtime publishing and sessions.
public sealed class FeaturesFixture : EvaluationFixture
{
    protected override void ConfigureInngest(InngestOptions options, Uri serverUrl)
    {
        base.ConfigureInngest(options, serverUrl);
        options.Streaming = true;
    }

    protected override void AddEvaluationFunctions(IInngestBuilder inngest) => FeatureFunctions.Add(inngest);
}
