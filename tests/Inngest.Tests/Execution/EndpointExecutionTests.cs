using System.Text.Json;
using Inngest.Client;
using Inngest.Execution;
using Inngest.Functions;
using Microsoft.Extensions.Logging;

namespace Inngest.Tests.Execution;

// A durable endpoint request that starts a run: its steps run in the request and wait in a buffer. The run is created
// when a step has to wait or fails, which hands it to Inngest, or after the response once the endpoint has finished.
public sealed class EndpointExecutionTests
{
    private const string RunId = "01JENDPOINTRUN00000000000000";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_request_that_runs_no_steps_completes_and_records_no_run()
    {
        using var host = new EngineHost();

        var outcome = await host.EndpointAsync(Endpoint(_ => Task.FromResult<object?>(Response("ok"))));

        outcome.Kind.ShouldBe(EndpointOutcomeKind.Completed);
        ((JsonElement)outcome.Output!).GetProperty("body").GetString().ShouldBe("ok");
        outcome.Finish.ShouldBeNull();
        host.EndpointCheckpoints.Saves.ShouldBeEmpty();
    }

    [Fact]
    public async Task Steps_run_in_the_request_and_the_run_is_recorded_once_the_endpoint_finished()
    {
        using var host = new EngineHost();
        var ran = new List<string>();

        var outcome = await host.EndpointAsync(
            Endpoint(async context =>
            {
                var order = await context.Step.RunAsync("load-order", () =>
                {
                    ran.Add("load-order");
                    return 42;
                });
                var total = await context.Step.RunAsync("reserve", () =>
                {
                    ran.Add("reserve");
                    return order + 1;
                });
                return Response($"total {total}");
            }),
            retries: 2);

        ran.ShouldBe(["load-order", "reserve"]);
        outcome.Kind.ShouldBe(EndpointOutcomeKind.Completed);
        outcome.RunId.ShouldBe(RunId);
        host.EndpointCheckpoints.Saves.ShouldBeEmpty();

        await outcome.Finish!(CancellationToken);

        var save = host.EndpointCheckpoints.Saves.ShouldHaveSingleItem();
        save.Created.ShouldBeTrue();
        save.RunId.ShouldBe(RunId);
        save.Retries.ShouldBe(2);
        save.Event!.ShouldContain("\"name\":\"inngest/http.request\"");
        save.OpNames.ShouldBe(["StepRun", "StepRun", "RunComplete"]);
        save.Ops[0].GetProperty("id").GetString().ShouldBe(StepIds.Hash("load-order"));
        save.Ops[0].GetProperty("data").GetInt32().ShouldBe(42);
        save.Ops[1].GetProperty("data").GetInt32().ShouldBe(43);
        save.Ops[2].GetProperty("id").GetString().ShouldBe(StepIds.Hash("complete"));
        save.Ops[2].GetProperty("data").GetProperty("body").GetString().ShouldBe("total 43");
    }

    [Fact]
    public async Task A_sleep_hands_the_run_to_Inngest_with_the_steps_so_far()
    {
        using var host = new EngineHost();

        var outcome = await host.EndpointAsync(Endpoint(async context =>
        {
            await context.Step.RunAsync("load-order", () => 42);
            await context.Step.SleepAsync("cool-off", TimeSpan.FromMinutes(5));
            return Response("after the sleep");
        }));

        outcome.Kind.ShouldBe(EndpointOutcomeKind.HandedOver);
        outcome.RunId.ShouldBe(RunId);
        outcome.Token.ShouldBe("eyJ.token");
        var saves = host.EndpointCheckpoints.Saves;
        saves.Select(save => save.Created).ShouldBe([true, false]);
        saves.Select(save => save.OpNames.ShouldHaveSingleItem()).ShouldBe(["StepRun", "Sleep"]);
        saves[1].Ops[0].GetProperty("id").GetString().ShouldBe(StepIds.Hash("cool-off"));
    }

    [Fact]
    public async Task A_step_that_fails_with_retries_left_goes_to_Inngest_as_a_step_error()
    {
        using var host = new EngineHost();
        Func<int> reserve = () => throw new InvalidOperationException("inventory service unavailable");

        var outcome = await host.EndpointAsync(Endpoint(async context =>
        {
            await context.Step.RunAsync("load-order", () => 42);
            await context.Step.RunAsync("reserve", reserve);
            return Response("reserved");
        }));

        outcome.Kind.ShouldBe(EndpointOutcomeKind.HandedOver);
        var saves = host.EndpointCheckpoints.Saves;
        saves.Select(save => save.OpNames.ShouldHaveSingleItem()).ShouldBe(["StepRun", "StepError"]);
        var error = saves[1].Ops[0];
        error.GetProperty("id").GetString().ShouldBe(StepIds.Hash("reserve"));
        error.GetProperty("error").GetProperty("message").GetString().ShouldBe("inventory service unavailable");
    }

    // Inngest takes more than one op in a call for steps in parallel, and would plan every later step of the run, which
    // starts the attempts of the failed one over.
    [Fact]
    public async Task Steps_that_ran_go_to_Inngest_one_to_a_call_when_a_later_step_fails()
    {
        using var host = new EngineHost();
        Func<int> charge = () => throw new InvalidOperationException("card declined");

        var outcome = await host.EndpointAsync(Endpoint(async context =>
        {
            await context.Step.RunAsync("load-order", () => 42);
            await context.Step.RunAsync("reserve", () => 43);
            await context.Step.RunAsync("charge", charge);
            return Response("charged");
        }));

        outcome.Kind.ShouldBe(EndpointOutcomeKind.HandedOver);
        var saves = host.EndpointCheckpoints.Saves;
        saves.Select(save => save.Created).ShouldBe([true, false, false]);
        saves.Select(save => save.OpNames.ShouldHaveSingleItem()).ShouldBe(["StepRun", "StepRun", "StepError"]);
        saves.Select(save => save.Ops[0].GetProperty("id").GetString())
            .ShouldBe([StepIds.Hash("load-order"), StepIds.Hash("reserve"), StepIds.Hash("charge")]);
    }

    [Fact]
    public async Task Steps_that_ran_before_large_ones_leave_the_call_that_creates_the_run_under_what_Inngest_reads()
    {
        using var host = new EngineHost();
        var large = new string('x', 1_500_000);

        var outcome = await host.EndpointAsync(Endpoint(async context =>
        {
            foreach (var id in new[] { "a", "b", "c" })
            {
                await context.Step.RunAsync(id, () => large);
            }

            await context.Step.SleepAsync("cool-off", TimeSpan.FromMinutes(5));
            return Response("later");
        }));

        outcome.Kind.ShouldBe(EndpointOutcomeKind.HandedOver);
        var saves = host.EndpointCheckpoints.Saves;
        saves.Select(save => save.OpNames.ShouldHaveSingleItem()).ShouldBe(["StepRun", "StepRun", "StepRun", "Sleep"]);
        saves.ShouldAllBe(save => (save.Event ?? "").Length + save.Steps.Length < CheckpointApiClient.MaxNewRunLength);
    }

    [Fact]
    public async Task A_step_that_fails_without_retries_goes_to_Inngest_as_a_final_failure()
    {
        using var host = new EngineHost();
        Func<int> reserve = () => throw new InvalidOperationException("inventory service unavailable");

        var outcome = await host.EndpointAsync(
            Endpoint(async context =>
            {
                await context.Step.RunAsync("reserve", reserve);
                return Response("reserved");
            }),
            retries: 0);

        outcome.Kind.ShouldBe(EndpointOutcomeKind.HandedOver);
        host.EndpointCheckpoints.Saves.ShouldHaveSingleItem().OpNames.ShouldBe(["StepFailed"]);
    }

    [Fact]
    public async Task An_endpoint_that_throws_with_attempts_left_is_retried_by_Inngest()
    {
        using var host = new EngineHost();

        var outcome = await host.EndpointAsync(Endpoint(_ => throw new InvalidOperationException("database timeout")));

        outcome.Kind.ShouldBe(EndpointOutcomeKind.HandedOver);
        var op = host.EndpointCheckpoints.Saves.ShouldHaveSingleItem().Ops.ShouldHaveSingleItem();
        op.GetProperty("op").GetString().ShouldBe("StepError");
        op.GetProperty("id").GetString().ShouldBe(StepIds.Hash("complete"));
        op.GetProperty("error").GetProperty("message").GetString().ShouldBe("database timeout");
    }

    [Fact]
    public async Task An_endpoint_that_fails_for_good_records_its_run_as_failed_after_the_response()
    {
        using var host = new EngineHost();

        var outcome = await host.EndpointAsync(Endpoint(async context =>
        {
            await context.Step.RunAsync("load-order", () => 42);
            throw new NonRetriableException("the order is closed");
        }));

        outcome.Kind.ShouldBe(EndpointOutcomeKind.Failed);
        outcome.Error.ShouldBeOfType<NonRetriableException>().Message.ShouldBe("the order is closed");
        host.EndpointCheckpoints.Saves.ShouldBeEmpty();

        await outcome.Finish!(CancellationToken);

        var save = host.EndpointCheckpoints.Saves.ShouldHaveSingleItem();
        save.OpNames.ShouldBe(["StepRun", "RunComplete"]);
        var result = save.Ops[1].GetProperty("data");
        result.GetProperty("status").GetInt32().ShouldBe(500);
        result.GetProperty("body").GetString()!.ShouldContain("the order is closed");
    }

    [Fact]
    public async Task A_final_failure_before_any_step_records_nothing()
    {
        using var host = new EngineHost();

        var outcome = await host.EndpointAsync(Endpoint(_ => throw new InvalidOperationException("bad input")), retries: 0);

        outcome.Kind.ShouldBe(EndpointOutcomeKind.Failed);
        outcome.Error.ShouldBeOfType<InvalidOperationException>();
        outcome.Finish.ShouldBeNull();
        host.EndpointCheckpoints.Saves.ShouldBeEmpty();
    }

    [Fact]
    public async Task Steps_in_parallel_go_to_Inngest_as_planned_steps()
    {
        using var host = new EngineHost();

        var outcome = await host.EndpointAsync(Endpoint(async context =>
        {
            await Task.WhenAll(context.Step.RunAsync("a", () => 1), context.Step.RunAsync("b", () => 2));
            return Response("both");
        }));

        outcome.Kind.ShouldBe(EndpointOutcomeKind.HandedOver);
        var save = host.EndpointCheckpoints.Saves.ShouldHaveSingleItem();
        save.OpNames.ShouldBe(["StepPlanned", "StepPlanned"]);
        save.Ops.Select(op => op.GetProperty("id").GetString()).ShouldBe([StepIds.Hash("a"), StepIds.Hash("b")]);
    }

    [Fact]
    public async Task Steps_in_parallel_after_a_step_that_ran_go_to_Inngest_together_in_a_call_of_their_own()
    {
        using var host = new EngineHost();

        var outcome = await host.EndpointAsync(Endpoint(async context =>
        {
            await context.Step.RunAsync("load-order", () => 42);
            await Task.WhenAll(context.Step.RunAsync("a", () => 1), context.Step.RunAsync("b", () => 2));
            return Response("both");
        }));

        outcome.Kind.ShouldBe(EndpointOutcomeKind.HandedOver);
        var saves = host.EndpointCheckpoints.Saves;
        saves.Select(save => save.Created).ShouldBe([true, false]);
        saves[0].OpNames.ShouldBe(["StepRun"]);
        saves[1].OpNames.ShouldBe(["StepPlanned", "StepPlanned"]);
    }

    [Fact]
    public async Task A_finished_run_too_large_for_one_call_is_recorded_in_calls_under_what_Inngest_reads()
    {
        using var host = new EngineHost();
        var large = new string('x', 1_500_000);

        var outcome = await host.EndpointAsync(Endpoint(async context =>
        {
            foreach (var id in new[] { "a", "b", "c" })
            {
                await context.Step.RunAsync(id, () => large);
            }

            return Response("done");
        }));
        await outcome.Finish!(CancellationToken);

        var saves = host.EndpointCheckpoints.Saves;
        saves.Count.ShouldBeGreaterThan(1);
        saves.Select(save => save.Created).ShouldBe([true, .. Enumerable.Repeat(false, saves.Count - 1)]);
        saves.SelectMany(save => save.OpNames).ShouldBe(["StepRun", "StepRun", "StepRun", "RunComplete"]);
        saves.ShouldAllBe(save => (save.Event ?? "").Length + save.Steps.Length < CheckpointApiClient.MaxNewRunLength);
    }

    [Fact]
    public async Task A_run_Inngest_cannot_take_over_fails_the_request()
    {
        using var host = new EngineHost();
        host.EndpointCheckpoints.FailNext(new InngestException("Inngest is unreachable"));

        var outcome = await host.EndpointAsync(Endpoint(async context =>
        {
            await context.Step.SleepAsync("wait", TimeSpan.FromSeconds(5));
            return Response("later");
        }));

        outcome.Kind.ShouldBe(EndpointOutcomeKind.Failed);
        outcome.Error.ShouldBeOfType<InngestException>().Message.ShouldBe("Inngest is unreachable");
        outcome.Finish.ShouldBeNull();
        host.LogsOf("Inngest.Execution.ExecutionEngine").ShouldContain(record => record.Id.Id == 151);
    }

    // A run that Inngest has without the step that waits has nothing queued, so the client leaving stops only the call
    // that creates the run.
    [Fact]
    public async Task A_client_that_leaves_once_Inngest_created_the_run_does_not_stop_its_hand_over()
    {
        using var host = new EngineHost();
        using var client = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        host.EndpointCheckpoints.Saved = save =>
        {
            if (save.Created)
            {
                client.Cancel();
            }
        };

        var outcome = await host.EndpointAsync(SleepAfterAStep(), requestAborted: client.Token);

        client.IsCancellationRequested.ShouldBeTrue();
        outcome.Kind.ShouldBe(EndpointOutcomeKind.HandedOver);
        host.EndpointCheckpoints.Saves.Select(save => save.OpNames.ShouldHaveSingleItem()).ShouldBe(["StepRun", "Sleep"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_hand_over_that_fails_once_Inngest_created_the_run_fails_the_request_and_names_the_run(bool clientLeft)
    {
        using var host = new EngineHost();
        using var client = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        host.EndpointCheckpoints.Saved = save =>
        {
            if (save.Created)
            {
                host.EndpointCheckpoints.FailNext(new InngestException("Inngest is unreachable"));
                if (clientLeft)
                {
                    client.Cancel();
                }
            }
        };

        var outcome = await host.EndpointAsync(SleepAfterAStep(), requestAborted: client.Token);

        outcome.Kind.ShouldBe(EndpointOutcomeKind.Failed);
        outcome.Error.ShouldBeOfType<InngestException>().Message.ShouldBe("Inngest is unreachable");
        outcome.Finish.ShouldBeNull();
        host.EndpointCheckpoints.Saves.Select(save => save.OpNames.ShouldHaveSingleItem()).ShouldBe(["StepRun"]);
        var logs = host.LogsOf("Inngest.Execution.ExecutionEngine");
        logs.ShouldNotContain(record => record.Id.Id == 151);
        var error = logs.Where(record => record.Id.Id == 154).ShouldHaveSingleItem();
        error.Level.ShouldBe(LogLevel.Error);
        error.Message.ShouldContain(RunId);
        error.Message.ShouldContain("POST /orders");
    }

    // The app stopping ends what is left of a hand-over, which otherwise goes on after the client left.
    [Fact]
    public async Task A_hand_over_left_by_its_client_ends_when_the_app_stops_and_names_the_run()
    {
        using var host = new EngineHost();
        using var client = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        using var stopped = new CancellationTokenSource();
        var held = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.EndpointCheckpoints.Saved = save =>
        {
            if (save.Created)
            {
                client.Cancel();
                held.TrySetResult(host.EndpointCheckpoints.HoldNext().Reached);
            }
        };

        var handOver = host.EndpointAsync(SleepAfterAStep(), requestAborted: client.Token, stopped: stopped.Token);
        await (await held.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken)).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);
        handOver.IsCompleted.ShouldBeFalse();
        await stopped.CancelAsync();
        var outcome = await handOver.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);

        outcome.Kind.ShouldBe(EndpointOutcomeKind.Failed);
        outcome.Error.ShouldBeAssignableTo<OperationCanceledException>();
        host.EndpointCheckpoints.Saves.Select(save => save.OpNames.ShouldHaveSingleItem()).ShouldBe(["StepRun"]);
        var error = host.LogsOf("Inngest.Execution.ExecutionEngine").Where(record => record.Id.Id == 154).ShouldHaveSingleItem();
        error.Message.ShouldContain(RunId);
        error.Message.ShouldContain("POST /orders");
    }

    // Inngest stores a new endpoint's function in the background of the call that creates its first run, and drops the
    // steps that reach it before that, such as the sleep that hands the run over.
    [Fact]
    public async Task The_first_hand_over_of_an_endpoint_adds_its_steps_a_second_after_it_created_the_run()
    {
        using var host = new EngineHost(waitForNewEndpoints: true);
        var waiting = WaitingAfterTheRunIsCreated(host);

        var handOver = host.EndpointAsync(SleepAfterAStep());
        await waiting;
        host.Time.Advance(TimeSpan.FromMilliseconds(999));
        handOver.IsCompleted.ShouldBeFalse();
        host.EndpointCheckpoints.Saves.ShouldHaveSingleItem().Created.ShouldBeTrue();
        host.Time.Advance(TimeSpan.FromMilliseconds(1));
        var outcome = await handOver.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);

        outcome.Kind.ShouldBe(EndpointOutcomeKind.HandedOver);
        host.EndpointCheckpoints.Saves.Select(save => save.OpNames.ShouldHaveSingleItem()).ShouldBe(["StepRun", "Sleep"]);
    }

    [Fact]
    public async Task The_first_finished_run_of_an_endpoint_recorded_in_more_than_one_call_adds_to_it_a_second_after_it_created_it()
    {
        using var host = new EngineHost(waitForNewEndpoints: true);
        var outcome = await host.EndpointAsync(ThreeLargeSteps());
        var waiting = WaitingAfterTheRunIsCreated(host);

        var recording = outcome.Finish!(CancellationToken);
        await waiting;
        host.Time.Advance(TimeSpan.FromMilliseconds(999));
        recording.IsCompleted.ShouldBeFalse();
        host.EndpointCheckpoints.Saves.ShouldHaveSingleItem().Created.ShouldBeTrue();
        host.Time.Advance(TimeSpan.FromMilliseconds(1));
        await recording.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);

        host.EndpointCheckpoints.Saves.SelectMany(save => save.OpNames).ShouldBe(["StepRun", "StepRun", "StepRun", "RunComplete"]);
    }

    [Fact]
    public async Task A_hand_over_a_second_after_the_endpoints_first_run_does_not_wait()
    {
        using var host = new EngineHost(waitForNewEndpoints: true);
        var waiting = WaitingAfterTheRunIsCreated(host);
        var first = host.EndpointAsync(SleepAfterAStep(), runId: "01JENDPOINTRUN0000000000000A");
        await waiting;
        host.Time.Advance(TimeSpan.FromSeconds(1));
        await first.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);
        host.EndpointCheckpoints.Saved = null;

        var outcome = await host.EndpointAsync(SleepAfterAStep(), runId: "01JENDPOINTRUN0000000000000B").WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);

        outcome.Kind.ShouldBe(EndpointOutcomeKind.HandedOver);
        host.EndpointCheckpoints.Saves.Where(save => save.RunId == outcome.RunId).Select(save => save.OpNames.ShouldHaveSingleItem()).ShouldBe(["StepRun", "Sleep"]);
    }

    [Fact]
    public async Task A_request_that_needs_one_call_does_not_wait_for_a_new_endpoint()
    {
        using var host = new EngineHost(waitForNewEndpoints: true);

        var handedOver = await host.EndpointAsync(Endpoint(async context =>
        {
            await context.Step.SleepAsync("cool-off", TimeSpan.FromMinutes(5));
            return Response("after the sleep");
        }), runId: "01JENDPOINTRUN0000000000000A").WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);
        var finished = await host.EndpointAsync(Endpoint(async context =>
        {
            await context.Step.RunAsync("load-order", () => 42);
            return Response("ok");
        }), runId: "01JENDPOINTRUN0000000000000B").WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);
        await finished.Finish!(CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);

        handedOver.Kind.ShouldBe(EndpointOutcomeKind.HandedOver);
        host.EndpointCheckpoints.Saves.Select(save => save.Created).ShouldBe([true, true]);
        host.Time.GetUtcNow().ShouldBe(EngineHost.Now);
    }

    // The run is not created yet, so nothing waits on the clock: the client leaving ends the request as before.
    [Fact]
    public async Task A_client_that_leaves_before_Inngest_created_the_run_ends_the_request_without_waiting()
    {
        using var host = new EngineHost(waitForNewEndpoints: true);
        await host.EndpointAsync(Endpoint(async context =>
        {
            await context.Step.SleepAsync("cool-off", TimeSpan.FromMinutes(5));
            return Response("after the sleep");
        }), runId: "01JENDPOINTRUN0000000000000A");
        using var client = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        var (reached, _) = host.EndpointCheckpoints.HoldNext();

        var request = host.EndpointAsync(SleepAfterAStep(), runId: "01JENDPOINTRUN0000000000000B", requestAborted: client.Token);
        await reached.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);
        await client.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(request.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken));
        host.EndpointCheckpoints.Saves.ShouldHaveSingleItem().RunId.ShouldBe("01JENDPOINTRUN0000000000000A");
    }

    // Once the run exists the client leaving does not end the wait, as with the rest of the hand-over, but the app
    // stopping does.
    [Fact]
    public async Task A_hand_over_waiting_for_a_new_endpoint_ends_when_the_app_stops()
    {
        using var host = new EngineHost(waitForNewEndpoints: true);
        using var client = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        using var stopped = new CancellationTokenSource();
        var waiting = WaitingAfterTheRunIsCreated(host);

        var handOver = host.EndpointAsync(SleepAfterAStep(), requestAborted: client.Token, stopped: stopped.Token);
        await waiting;
        await client.CancelAsync();
        handOver.IsCompleted.ShouldBeFalse();
        await stopped.CancelAsync();
        var outcome = await handOver.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);

        outcome.Kind.ShouldBe(EndpointOutcomeKind.Failed);
        host.EndpointCheckpoints.Saves.ShouldHaveSingleItem().Created.ShouldBeTrue();
        host.LogsOf("Inngest.Execution.ExecutionEngine").Where(record => record.Id.Id == 154).ShouldHaveSingleItem().Message.ShouldContain(RunId);
    }

    [Fact]
    public async Task A_run_Inngest_takes_over_without_a_token_fails_the_request()
    {
        using var host = new EngineHost();
        host.EndpointCheckpoints.Token = null;

        var outcome = await host.EndpointAsync(Endpoint(async context =>
        {
            await context.Step.SleepAsync("wait", TimeSpan.FromSeconds(5));
            return Response("later");
        }));

        outcome.Kind.ShouldBe(EndpointOutcomeKind.Failed);
        outcome.Error.ShouldBeOfType<InngestException>().Message.ShouldContain("sent no token");
    }

    [Fact]
    public async Task A_run_that_cannot_be_recorded_after_the_response_is_only_logged()
    {
        using var host = new EngineHost();
        var outcome = await host.EndpointAsync(Endpoint(async context =>
        {
            await context.Step.RunAsync("load-order", () => 42);
            return Response("ok");
        }));
        host.EndpointCheckpoints.FailNext(new InngestException("Inngest is unreachable"));

        await outcome.Finish!(CancellationToken);

        host.EndpointCheckpoints.Saves.ShouldBeEmpty();
        host.LogsOf("Inngest.Execution.ExecutionEngine").ShouldContain(record => record.Id.Id == 152);
    }

    private static FunctionDefinition Endpoint(Func<FunctionContext<JsonElement>, Task<object?>> handler) =>
        TestFunction.Define(handler, "POST /orders");

    private static FunctionDefinition ThreeLargeSteps() => Endpoint(async context =>
    {
        foreach (var id in new[] { "a", "b", "c" })
        {
            await context.Step.RunAsync(id, () => new string('x', 1_500_000));
        }

        return Response("done");
    });

    // Completes once the request has created its run and waits on the clock.
    private static Task WaitingAfterTheRunIsCreated(EngineHost host)
    {
        var timer = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.EndpointCheckpoints.Saved = save =>
        {
            if (save.Created)
            {
                timer.TrySetResult(host.Time.TimersCreated(host.Time.TimerCount + 1));
            }
        };

        return timer.Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);
    }

    private static FunctionDefinition SleepAfterAStep() => Endpoint(async context =>
    {
        await context.Step.RunAsync("load-order", () => 42);
        await context.Step.SleepAsync("cool-off", TimeSpan.FromMinutes(5));
        return Response("after the sleep");
    });

    // What the ASP.NET Core middleware returns as the endpoint's output: the response it captured.
    private static JsonElement Response(string body)
    {
        using var document = JsonDocument.Parse(
            $$"""{"status":200,"headers":{"content-type":"application/json"},"body":{{JsonSerializer.Serialize(body)}}}""");
        return document.RootElement.Clone();
    }
}
