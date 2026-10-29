using Inngest.IntegrationTests.Evaluation.Harness;
using Inngest.IntegrationTests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.Gateway;

[Trait("Category", "Evaluation")]
[Trait("Area", "Signals, fetch and AI inference")]
public sealed class GatewayTests(EvaluationEnvironment environment) : EvaluationScenario<GatewayFixture>(environment)
{
    // How the trace names the op; the span shows WAITING once Inngest has stored the wait.
    private const string WaitForSignalOp = "WAIT_FOR_SIGNAL";

    private IInngestClient Client => Fixture.App.Services.GetRequiredService<IInngestClient>();

    [Fact(Timeout = 60_000)]
    public async Task A_run_waiting_for_a_signal_resumes_when_the_signal_is_sent()
    {
        var signal = $"approval/{TestId}";
        var eventId = await SendAsync(GatewayFunctions.WaitForSignal, new SignalWait(TestId, signal, TimeoutSeconds: 60));
        var run = await WaitForRunAsync(eventId);

        var resumed = await SendSignalOnceWaitedForAsync(signal, new Approval("alice"));
        var ended = await WaitForEndAsync(eventId);

        resumed.ShouldBe(run.RunId);
        ended.Status.ShouldBe(RunStatus.Completed);
        ended.OutputAs<SignalOutcome>().ShouldBe(new SignalOutcome(true, signal, "alice"));
    }

    [Fact(Timeout = 60_000)]
    public async Task A_wait_for_a_signal_that_never_comes_times_out_to_null()
    {
        var run = await RunToEndAsync(GatewayFunctions.WaitForSignal, new SignalWait(TestId, $"never/{TestId}", TimeoutSeconds: 2), TimeSpan.FromSeconds(30));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<SignalOutcome>().ShouldBe(new SignalOutcome(false, null, null));
    }

    [Fact(Timeout = 60_000)]
    public async Task A_second_wait_for_a_signal_fails_its_run_unless_it_replaces_the_first()
    {
        var signal = $"approval/{TestId}";
        var firstId = await SendAsync(GatewayFunctions.WaitForSignal, new SignalWait(TestId, signal, TimeoutSeconds: 60));
        var first = await WaitForRunAsync(firstId);
        await WaitForStepAsync(first.RunId, WaitForSignalOp);

        var failed = await RunToEndAsync(GatewayFunctions.WaitForSignal, new SignalWait(TestId, signal, TimeoutSeconds: 60));
        var replacingId = await SendAsync(GatewayFunctions.WaitForSignal, new SignalWait(TestId, signal, TimeoutSeconds: 60, SignalConflict.Replace));
        var replacing = await WaitForRunAsync(replacingId);
        await WaitForStepAsync(replacing.RunId, WaitForSignalOp);
        var resumed = await Client.SendSignalAsync(new InngestSignal(signal, new Approval("dave")), Cancellation);
        var ended = await WaitForEndAsync(replacingId);

        failed.Status.ShouldBe(RunStatus.Failed);
        // The server fails the run itself, so the error is wrapped the way the server stores its own errors.
        failed.Output!.Value.GetProperty("error").GetProperty("message").GetString()
            .ShouldBe("Signal conflict; signal wait already exists for another run");
        resumed.ShouldBe(replacing.RunId);
        ended.OutputAs<SignalOutcome>().ShouldBe(new SignalOutcome(true, signal, "dave"));

        // The replaced wait stays until its timeout, but the signal no longer reaches it.
        (await Api.GetRunAsync(first.RunId, Cancellation)).HasEnded.ShouldBeFalse();
        (await Client.SendSignalAsync(new InngestSignal(signal, new Approval("erin")), Cancellation)).ShouldBeNull();
    }

    [Fact(Timeout = 60_000)]
    public async Task A_signal_no_run_waits_for_returns_null()
    {
        var runId = await Client.SendSignalAsync(new InngestSignal($"nobody/{TestId}", new Approval("bob")), Cancellation);

        runId.ShouldBeNull();
    }

    [Fact(Timeout = 60_000)]
    public async Task A_signal_sent_from_a_step_resumes_the_waiting_run_and_returns_its_id()
    {
        var signal = $"approval/{TestId}";
        var waitId = await SendAsync(GatewayFunctions.WaitForSignal, new SignalWait(TestId, signal, TimeoutSeconds: 60));
        var waiting = await WaitForRunAsync(waitId);
        await WaitForStepAsync(waiting.RunId, WaitForSignalOp);

        var sender = await RunToEndAsync(GatewayFunctions.SendSignal, new SignalSend(TestId, signal, "carol"));
        var ended = await WaitForEndAsync(waitId);

        sender.OutputAs<string>().ShouldBe(waiting.RunId);
        ended.OutputAs<SignalOutcome>().ShouldBe(new SignalOutcome(true, signal, "carol"));
    }

    [Fact(Timeout = 60_000)]
    public async Task Inngest_makes_a_fetch_request_once_and_the_run_reads_its_response()
    {
        var run = await RunToEndAsync(
            GatewayFunctions.Fetch,
            new FetchCall(TestId, Fixture.ServerUrl(FakeEndpoints.EchoPath), Fixture.LocalUrl(FakeEndpoints.EchoPath)),
            TimeSpan.FromSeconds(30));

        run.Status.ShouldBe(RunStatus.Completed);
        var outcome = run.OutputAs<FetchOutcome>();
        outcome.Status.ShouldBe(201);
        outcome.EchoHeader.ShouldBe("yes");
        outcome.ContentType.ShouldBe("application/json");
        outcome.Echo.Method.ShouldBe("POST");
        outcome.Echo.PathAndQuery.ShouldBe("/fake/echo?source=step");
        outcome.Echo.Body.ShouldBe("""{"hello":"world"}""");
        outcome.Echo.Header("Content-Type").ShouldBe("application/json; charset=utf-8");
        outcome.Echo.Header(FakeEndpoints.TestIdHeader).ShouldBe(TestId);

        // The function replayed past the request twice more, and Inngest made it once.
        Observed.Named(FakeEndpoints.EchoPath).Count.ShouldBe(1);
        Observed.Named("after").Count.ShouldBe(1);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_client_with_the_step_fetch_handler_turns_requests_in_the_function_into_steps_and_leaves_step_handlers_alone()
    {
        var run = await RunToEndAsync(
            GatewayFunctions.FetchThroughClient,
            new FetchCall(TestId, Fixture.ServerUrl(FakeEndpoints.EchoPath), Fixture.LocalUrl(FakeEndpoints.EchoPath)),
            TimeSpan.FromSeconds(30));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<ClientOutcome>().ShouldBe(new ClientOutcome(201, 201));
        var received = Observed.Values<ReceivedRequest>(FakeEndpoints.EchoPath);
        received.Select(request => request.PathAndQuery).Order().ShouldBe(["/fake/echo?source=function", "/fake/echo?source=step-handler"]);
    }

    [Fact(Timeout = 60_000)]
    public async Task Inngest_sends_an_inference_request_to_the_provider_and_the_run_reads_the_completion()
    {
        var run = await RunToEndAsync(GatewayFunctions.Infer, new InferCall(TestId, Fixture.ServerUrl(FakeEndpoints.ChatPath)), TimeSpan.FromSeconds(30));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<string>().ShouldBe(FakeEndpoints.Reply);
        var request = Observed.Values<ReceivedRequest>(FakeEndpoints.ChatPath).ShouldHaveSingleItem();
        request.Method.ShouldBe("POST");
        request.Header("Authorization").ShouldBe($"Bearer {GatewayFunctions.FakeKey}");
        request.Body.ShouldContain("\"model\":\"fake-model\"");
        request.Body.ShouldContain("Say hello");
    }

    // Until Inngest has stored the wait, nothing waits for the signal and the send returns null.
    private Task<string?> SendSignalOnceWaitedForAsync(string signal, object data) =>
        Poll.UntilAsync(
            $"signal {signal} to resume a run",
            token => Client.SendSignalAsync(new InngestSignal(signal, data), token),
            runId => runId is not null,
            TimeSpan.FromSeconds(15),
            Fixture.DescribeAsync,
            Cancellation);
}
