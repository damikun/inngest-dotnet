using Inngest.IntegrationTests.Evaluation.Harness;

namespace Inngest.IntegrationTests.Execution;

// A dev server and an app with the functions of the execution tests: cancel handlers, error codes and the ids of the
// events a step sends.
public sealed class ExecutionFixture : EvaluationFixture
{
    protected override void AddEvaluationFunctions(IInngestBuilder inngest)
    {
        CancelHandlerFunctions.Add(inngest);
        ErrorCodeFunctions.Add(inngest);
        StableIdFunctions.Add(inngest);
    }
}

// An app that encrypts its payloads, with a middleware in front of encryption that records what Inngest sends.
public sealed class EncryptedExecutionFixture : EvaluationFixture
{
    public override string AppId => "eval-encrypted";

    protected override void AddEvaluationFunctions(IInngestBuilder inngest) => EncryptedInvokeFunctions.Add(inngest);
}
