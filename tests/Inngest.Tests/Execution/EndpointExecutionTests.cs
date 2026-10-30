using System.Text.Json;
using Inngest.Execution;
using Inngest.Functions;

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
        var save = host.EndpointCheckpoints.Saves.ShouldHaveSingleItem();
        save.Created.ShouldBeTrue();
        save.OpNames.ShouldBe(["StepRun", "Sleep"]);
        save.Ops[1].GetProperty("id").GetString().ShouldBe(StepIds.Hash("cool-off"));
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
        var save = host.EndpointCheckpoints.Saves.ShouldHaveSingleItem();
        save.OpNames.ShouldBe(["StepRun", "StepError"]);
        save.Ops[1].GetProperty("id").GetString().ShouldBe(StepIds.Hash("reserve"));
        save.Ops[1].GetProperty("error").GetProperty("message").GetString().ShouldBe("inventory service unavailable");
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

    // What the ASP.NET Core middleware returns as the endpoint's output: the response it captured.
    private static JsonElement Response(string body)
    {
        using var document = JsonDocument.Parse(
            $$"""{"status":200,"headers":{"content-type":"application/json"},"body":{{JsonSerializer.Serialize(body)}}}""");
        return document.RootElement.Clone();
    }
}
