using Inngest.IntegrationTests.Evaluation.Harness;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.Checkpointing;

// A dev server and an app that turns checkpointing on for every function, except the one that opts out to compare.
public sealed class CheckpointingFixture : EvaluationFixture
{
    // The calls the app answered for one function, which is one run when a scenario uses the function once.
    public IReadOnlyList<ServedRequest> CallsOf(string functionId) =>
        [.. App.Requests.Snapshot().Where(request => request.Method == "POST" && request.Target.Contains($"fnId={AppId}-{functionId}&", StringComparison.Ordinal))];

    protected override void ConfigureInngest(InngestOptions options, Uri serverUrl)
    {
        base.ConfigureInngest(options, serverUrl);
        options.Checkpointing = new CheckpointingOptions();
    }

    protected override void AddEvaluationFunctions(IInngestBuilder inngest) => CheckpointingFunctions.Add(inngest);
}
