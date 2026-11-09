using System.Text.Json;
using Inngest.Execution;
using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Evaluation.Harness;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.Execution;

public sealed class ExecutionTests(EvaluationEnvironment environment) : EvaluationScenario<ExecutionFixture>(environment)
{
    private static readonly TimeSpan HandlerWait = TimeSpan.FromSeconds(30);

    [Theory(Timeout = 90_000)]
    [InlineData(CancelHandlerFunctions.Start, CancelHandlerFunctions.Stop, "eval-cancel-handled")]
    [InlineData(CancelHandlerFunctions.StartDelegate, CancelHandlerFunctions.StopDelegate, "eval-cancel-delegate")]
    public async Task A_cancelled_run_starts_its_cancel_handler_with_the_run_and_its_event(string start, string stop, string functionId)
    {
        var eventId = await SendAsync(start, new RunEvent(TestId));
        var run = await WaitForRunAsync(eventId);
        await WaitForStepAsync(run.RunId, StepOps.Sleep);

        await SendAsync(stop, new RunEvent(TestId));
        var ended = await WaitForEndAsync(eventId);

        ended.Status.ShouldBe(RunStatus.Cancelled);
        var seen = (await WaitForObservationsAsync("on-cancel", timeout: HandlerWait)).ShouldHaveSingleItem().Value.ShouldBeOfType<CancelSeen>();
        seen.ShouldBe(new CancelSeen(SystemEvents.FunctionCancelled, functionId, run.RunId, start));
    }

    [Fact(Timeout = 60_000)]
    public async Task The_code_of_a_failed_step_comes_back_from_inngest()
    {
        var run = await RunToEndAsync(ErrorCodeFunctions.Step, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<CodeSeen>().ShouldBe(new CodeSeen(nameof(CardDeclinedException), "card_declined"));
    }

    // Inngest rebuilds the error a run failed with from its name, message and stack, so a failure handler gets no code.
    // If this starts to fail, Inngest keeps codes now, and the errors guide can say so.
    [Fact(Timeout = 60_000)]
    public async Task The_code_of_a_failed_run_does_not_reach_its_failure_handler()
    {
        var run = await RunToEndAsync(ErrorCodeFunctions.Function, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Failed);
        var seen = (await WaitForObservationsAsync("on-failure", timeout: HandlerWait)).ShouldHaveSingleItem().Value.ShouldBeOfType<CodeSeen>();
        seen.ShouldBe(new CodeSeen(nameof(NonRetriableException), null));
    }

    [Fact(Timeout = 60_000)]
    public async Task A_step_that_sends_again_after_its_result_was_lost_starts_the_receiver_once()
    {
        var run = await RunToEndAsync(StableIdFunctions.Send, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        Observed.Values<int>("sent").ShouldBe([0, 1]);
        var received = await WaitForObservationsAsync("received");
        await Task.Delay(NoRunWindow, Cancellation);
        Observed.Named("received").Count.ShouldBe(1);
        received[0].Value.ShouldBe($"{run.RunId}-{StepIds.Hash(StableIdFunctions.StepId)}-0");
    }
}

public sealed class EncryptedInvokeTests(EvaluationEnvironment environment) : EvaluationScenario<EncryptedExecutionFixture>(environment)
{
    [Fact(Timeout = 60_000)]
    public async Task The_data_of_an_invoke_reaches_inngest_encrypted_and_the_child_reads_it_decrypted()
    {
        var run = await RunToEndAsync(EncryptedInvokeFunctions.Parent, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        Observed.Values<string>("child-read").ShouldBe([EncryptedInvokeFunctions.Secret]);
        var sent = Observed.Values<string>("sent-as").ShouldHaveSingleItem();
        sent.ShouldNotContain(EncryptedInvokeFunctions.Secret);
        using var envelope = JsonDocument.Parse(sent);
        envelope.RootElement.GetProperty("__ENCRYPTED__").GetBoolean().ShouldBeTrue();
        envelope.RootElement.GetProperty("__STRATEGY__").GetString().ShouldBe("inngest/libsodium");
    }
}
