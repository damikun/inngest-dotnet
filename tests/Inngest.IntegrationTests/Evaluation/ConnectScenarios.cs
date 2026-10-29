using System.Collections.Concurrent;
using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Evaluation.Harness;
using Inngest.IntegrationTests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.Evaluation;

// The app runs as a Connect worker with no HTTP endpoint: every call reaches it over its WebSocket to the gateway.
[Trait("Category", "Evaluation")]
[Trait("Area", "Connect")]
public sealed class ConnectScenarios(EvaluationEnvironment environment) : EvaluationScenario<EvaluationConnectFixture>(environment)
{
    private const string RestartAppId = "eval-connect-restart";

    [Fact(Timeout = 60_000)]
    public async Task A_worker_runs_steps_parallel_steps_a_sleep_and_a_wait_over_its_websocket()
    {
        var eventId = await SendAsync(ConnectWorkerFunctions.Flow, new ValueEvent(TestId, 2));
        var run = await WaitForRunAsync(eventId);
        await WaitForStepAsync(run.RunId, StepOps.WaitForEvent, timeout: TimeSpan.FromSeconds(20));

        await SendAsync(ConnectWorkerFunctions.Resume, new Resume(TestId, "over the socket"));
        var ended = await WaitForEndAsync(eventId, TimeSpan.FromSeconds(30));

        ended.Status.ShouldBe(RunStatus.Completed);
        var result = ended.OutputAs<ConnectFlowResult>();
        result.Start.ShouldBe(3);
        result.Parts.ShouldBe([3, 6, 9]);
        result.Resumed.ShouldBe("over the socket");
        result.Total.ShouldBe(18);
        Observed.Named("start").Count.ShouldBe(1);
        Observed.Values<int>("part").ShouldBe([1, 2, 3], ignoreOrder: true);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_worker_invokes_a_function_and_sends_an_event_that_starts_another()
    {
        var run = await RunToEndAsync(ConnectWorkerFunctions.Parent, new ValueEvent(TestId, 6), TimeSpan.FromSeconds(30));

        run.Status.ShouldBe(RunStatus.Completed);
        var result = run.OutputAs<ConnectParentResult>();
        result.Child.ShouldBe(42);
        var received = await WaitForEndAsync(result.Sent.ShouldHaveSingleItem());
        received.OutputAs<int>().ShouldBe(42);
        Observed.Values<int>("received").ShouldBe([42]);
    }

    [Fact(Timeout = 90_000)]
    public async Task A_worker_handles_fifty_runs_at_once()
    {
        var ids = await SendManyAsync(Enumerable.Range(1, 50).Select(value => new EventPayload(ConnectWorkerFunctions.Many, new ValueEvent(TestId, value))));

        await WaitForObservationsAsync("done", count: 50, timeout: TimeSpan.FromSeconds(60));
        var runs = new ConcurrentBag<RunInfo>();
        await Parallel.ForEachAsync(
            ids,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = Cancellation },
            async (id, _) => runs.Add(await WaitForEndAsync(id, TimeSpan.FromSeconds(30))));

        runs.Count.ShouldBe(50);
        runs.ShouldAllBe(run => run.Status == RunStatus.Completed);
        runs.Select(run => run.OutputAs<int>()).Order().ShouldBe(Enumerable.Range(1, 50).Select(value => (value * 2) + 1));
    }

    [Fact(Timeout = 60_000)]
    public async Task A_function_failing_on_a_worker_is_retried_and_its_failure_handler_runs()
    {
        var run = await RunToEndAsync(ConnectWorkerFunctions.Failing, new RunEvent(TestId), TimeSpan.FromSeconds(30));

        run.Status.ShouldBe(RunStatus.Failed);
        run.Error.Message.ShouldBe("fails over the WebSocket too");
        Observed.Values<int>("attempt").ShouldBe([0, 1]);
        (await WaitForObservationsAsync("on-failure", timeout: TimeSpan.FromSeconds(20))).ShouldHaveSingleItem().Value.ShouldBe("fails over the WebSocket too");
    }

    [Fact(Timeout = 120_000)]
    public async Task A_run_on_a_worker_whose_state_passes_4_MB_loads_it_through_the_api()
    {
        var run = await RunToEndAsync(ConnectWorkerFunctions.BigState, new RunEvent(TestId), TimeSpan.FromSeconds(60));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<BigStateResult>().Hashes.ShouldBe(
            [.. Enumerable.Range(1, 3).Select(index => StepDataFunctions.Sha256(StepDataFunctions.LargeText($"{TestId}-{index}", LargeStateFunctions.ChunkLength)))]);
        Fixture.Worker.Services.GetRequiredService<OutgoingRequests>().Snapshot()
            .ShouldContain(request => request.PathAndQuery == $"/v0/runs/{run.RunId}/actions" && request.StatusCode == 200);
    }

    // The worker stops while the run sleeps; a new worker for the same app connects and the run finishes on it.
    [Fact(Timeout = 90_000)]
    public async Task A_run_sleeping_while_its_worker_is_replaced_finishes_on_the_new_worker()
    {
        var probe = new Probe();
        void Services(IServiceCollection services)
        {
            services.AddSingleton(probe);
            services.AddSingleton<AppInstance>();
        }

        var first = await Fixture.StartWorkerAsync(RestartAppId, ConnectWorkerFunctions.AddRestart, Cancellation, Services);
        await Fixture.WaitForSyncAsync(RestartAppId, first, Cancellation);
        var firstInstance = first.Services.GetRequiredService<AppInstance>().Id;
        var eventId = await SendAsync(ConnectWorkerFunctions.Restart, new RunEvent(TestId));
        var run = await WaitForRunAsync(eventId);
        await WaitForStepAsync(run.RunId, StepOps.Sleep, timeout: TimeSpan.FromSeconds(20));

        await first.StopAsync(Cancellation);
        var second = await Fixture.StartWorkerAsync(RestartAppId, ConnectWorkerFunctions.AddRestart, Cancellation, Services);
        var ended = await WaitForEndAsync(eventId, TimeSpan.FromSeconds(45));

        ended.Status.ShouldBe(RunStatus.Completed);
        var secondInstance = second.Services.GetRequiredService<AppInstance>().Id;
        ended.OutputAs<string>().ShouldBe(secondInstance);
        probe.For(TestId).Values<string>("before").ShouldBe([firstInstance]);
        probe.For(TestId).Values<string>("after").ShouldBe([secondInstance]);
    }
}

// A worker in cloud mode against a self-hosted server, during a signing key rotation: the server only knows the old key,
// the worker has the new one and the old one as its fallback.
[Trait("Category", "Evaluation")]
[Trait("Area", "Connect")]
public sealed class CloudConnectScenarios(EvaluationEnvironment environment) : EvaluationScenario<CloudConnectFixture>(environment)
{
    [Fact(Timeout = 60_000)]
    public async Task A_cloud_worker_connects_with_its_fallback_key_and_runs_functions()
    {
        var client = Fixture.Worker.Services.GetRequiredService<IInngestClient>();
        var sent = await client.SendAsync(new InngestEvent(ConnectWorkerFunctions.CloudSteps, new ValueEvent(TestId, 20)), Cancellation);
        var run = await WaitForEndAsync(sent.Ids.ShouldHaveSingleItem(), TimeSpan.FromSeconds(30));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<int>().ShouldBe(42);

        // The start call with the new key was refused, and the worker went on with the old one.
        var starts = Fixture.Worker.Services.GetRequiredService<OutgoingRequests>().Snapshot()
            .Where(request => request.PathAndQuery == "/v0/connect/start")
            .ToList();
        starts.ShouldContain(request => request.Authorization == $"Bearer {CloudScenarios.HashedKey(Fixture.NewKey)}" && request.StatusCode == 401);
        starts.ShouldContain(request => request.Authorization == $"Bearer {CloudScenarios.HashedKey(Fixture.OldKey)}" && request.StatusCode == 200);
    }
}
