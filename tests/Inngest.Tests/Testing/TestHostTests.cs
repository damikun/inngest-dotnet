using System.Text.Json;
using Inngest.Testing;
using Microsoft.Extensions.DependencyInjection;
using static Inngest.Tests.Testing.TestHosts;

namespace Inngest.Tests.Testing;

// Whole runs on the real engine: the host replays the function until it returns, so step results go through JSON
// and code outside steps runs once per call, as it does under Inngest.
public sealed class TestHostTests
{
    [Fact]
    public async Task Sequential_steps_run_once_each_while_the_function_replays()
    {
        var bodyRuns = 0;
        var handlerRuns = new List<string>();
        await using var host = For(async context =>
        {
            bodyRuns++;
            var a = await context.Step.RunAsync("a", () =>
            {
                handlerRuns.Add("a");
                return 1;
            });
            var b = await context.Step.RunAsync("b", () =>
            {
                handlerRuns.Add("b");
                return a + 1;
            });
            return new { total = a + b };
        });

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        run.Output!.Value.GetProperty("total").GetInt32().ShouldBe(3);
        run.Steps.Select(step => step.Id).ShouldBe(["a", "b"]);
        run.Steps.ShouldAllBe(step => step.Status == TestStepStatus.Completed && step.Attempts == 1 && step.Type == StepType.Run && !step.Mocked);
        run.GetStep("b").GetOutput<int>().ShouldBe(2);
        handlerRuns.ShouldBe(["a", "b"]);
        bodyRuns.ShouldBe(3);
        run.Error.ShouldBeNull();
        run.WaitingOn.ShouldBeEmpty();
    }

    [Fact]
    public async Task Step_results_reach_the_function_through_json()
    {
        await using var host = For(async context =>
        {
            var order = await context.Step.RunAsync("load", () => new Order("o_1", 12.5m, DateTimeOffset.UnixEpoch));
            return order;
        });

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.GetOutput<Order>().ShouldBe(new Order("o_1", 12.5m, DateTimeOffset.UnixEpoch));
        run.GetStep("load").Output!.Value.GetProperty("id").GetString().ShouldBe("o_1");
    }

    [Fact]
    public async Task Parallel_steps_run_in_targeted_calls_and_the_function_resumes_when_all_are_done()
    {
        RecordingEngine engine = null!;
        await using var host = new InngestTestHost(
            Services(async context =>
            {
                var results = await Task.WhenAll(
                    context.Step.RunAsync("a", () => "A"),
                    context.Step.RunAsync("b", () => "B"));
                var c = await context.Step.RunAsync("c", () => "C");
                return string.Concat(results) + c;
            }),
            inner => engine = new RecordingEngine(inner));

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.GetOutput<string>().ShouldBe("ABC");
        run.Steps.Select(step => step.Id).ShouldBe(["a", "b", "c"]);
        run.Steps.ShouldAllBe(step => step.Attempts == 1);

        // Planned steps run one targeted call each with immediate execution off; after the group, "c" runs inline.
        engine.Targets.ShouldBe(["step", Hash("a"), Hash("b"), "step", "step"]);
        engine.Bodies.Select(body => body.GetProperty("ctx").GetProperty("disable_immediate_execution").GetBoolean())
            .ShouldBe([false, true, true, false, false]);
    }

    [Fact]
    public async Task A_race_resolves_with_the_result_that_completed_first()
    {
        await using var host = For(async context =>
        {
            var first = context.Step.RunAsync("first", () => "first");
            var second = context.Step.RunAsync("second", () => "second");
            var winner = await Task.WhenAny(second, first);
            return await winner;
        });

        var run = await host.RunAsync("fn", Event(), Cancel);

        // Both ran, in the order they were reported, so "first" completed first and wins on replay.
        run.GetOutput<string>().ShouldBe("first");
        run.Steps.ShouldAllBe(step => step.Status == TestStepStatus.Completed);
    }

    [Fact]
    public async Task The_last_call_sends_what_inngest_would_have_memoized()
    {
        RecordingEngine engine = null!;
        await using var host = new InngestTestHost(
            Services(async context =>
            {
                await context.Step.RunAsync("a", () => new { value = 1 });
                await context.Step.SleepAsync("nap", TimeSpan.FromMinutes(5));
                return null;
            }, retries: 2),
            inner => engine = new RecordingEngine(inner));

        var run = await host.RunAsync("fn", Event(new { orderId = "o_1" }), new StepMocks().Sleep("nap"), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        var body = engine.Bodies[^1];
        var ctx = body.GetProperty("ctx");
        ctx.GetProperty("attempt").GetInt32().ShouldBe(0);
        ctx.GetProperty("max_attempts").GetInt32().ShouldBe(3);
        ctx.GetProperty("run_id").GetString().ShouldBe(run.RunId);
        ctx.GetProperty("stack").GetProperty("stack").EnumerateArray().Select(id => id.GetString()).ShouldBe([Hash("a"), Hash("nap")]);
        body.GetProperty("steps").GetProperty(Hash("a")).GetRawText().ShouldBe("""{"data":{"value":1}}""");
        body.GetProperty("steps").GetProperty(Hash("nap")).ValueKind.ShouldBe(JsonValueKind.Null);
        body.GetProperty("event").GetProperty("data").GetProperty("orderId").GetString().ShouldBe("o_1");
        body.GetProperty("event").GetProperty("name").GetString().ShouldBe(EventName);
        body.GetProperty("events").GetArrayLength().ShouldBe(1);
        body.GetProperty("version").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task A_class_based_function_gets_its_services_from_the_container()
    {
        var services = new ServiceCollection();
        services.AddScoped<IGreetings, Greetings>();
        services.AddInngest(options => options.AppId = AppId)
            .AddFunction<Greeter>(new FunctionOptions { Id = "greeter", Triggers = [Trigger.Event("user/signed.up")] });
        await using var host = new InngestTestHost(services);

        var run = await host.RunAsync("greeter", new InngestEvent("user/signed.up", new SignedUp("Ada")), Cancel);

        run.GetOutput<Greeting>().ShouldBe(new Greeting("Hello, Ada"));
        run.GetStep("greet").GetOutput<string>().ShouldBe("Hello, Ada");
    }

    [Fact]
    public async Task A_batch_reaches_the_function_as_its_events()
    {
        var services = new ServiceCollection();
        services.AddInngest(options => options.AppId = AppId).AddFunction<Payment>(
            new FunctionOptions { Id = "sum", Triggers = [Trigger.Event("payment/made")], BatchEvents = new BatchEvents(10, TimeSpan.FromSeconds(5)) },
            async (context, _) => await context.Step.RunAsync("sum", () => context.Events.Sum(payment => payment.Data.Amount)));
        await using var host = new InngestTestHost(services);

        var run = await host.RunAsync(
            "sum",
            [new InngestEvent("payment/made", new Payment(5)), new InngestEvent("payment/made", new Payment(7)), new InngestEvent("payment/made", new Payment(30))],
            Cancel);

        run.GetOutput<int>().ShouldBe(42);
    }

    [Fact]
    public async Task The_apps_middleware_runs_around_the_function_and_its_steps()
    {
        var log = new HookLog();
        await using var host = For(
            async context =>
            {
                var value = await context.Step.RunAsync("a", () => "real");
                await context.Step.RunAsync("b", () => "b");
                return value;
            },
            configure: inngest =>
            {
                inngest.Services.AddSingleton(log);
                inngest.AddMiddleware<Hooks>();
            });

        var run = await host.RunAsync("fn", Event(), new StepMocks().Run("a", "mocked"), Cancel);

        run.GetOutput<string>().ShouldBe("mocked");
        run.GetStep("a").Mocked.ShouldBeTrue();

        // A mock replaces only the handler: the app's wrapper still runs around it and sees its value.
        log.Entries.ShouldBe(
        [
            "run start",
            "step start a",
            "wrap handler a -> mocked",
            "step complete a",
            "step start b",
            "wrap handler b -> b",
            "step complete b",
            "run complete mocked",
        ]);
    }

    [Fact]
    public async Task Events_sent_from_steps_are_recorded_and_get_ids()
    {
        await using var host = For(async context =>
        {
            var sent = await context.Step.SendEventAsync("notify", new InngestEvent("user/notified", new Notified(7)));
            return sent.Ids;
        });

        var run = await host.RunAsync("fn", Event(), Cancel);

        var @event = run.SentEvents.ShouldHaveSingleItem();
        @event.Name.ShouldBe("user/notified");
        @event.Data.ShouldBe(new Notified(7));
        host.SentEvents.ShouldBe(run.SentEvents);
        run.GetOutput<string[]>()!.ShouldHaveSingleItem().Length.ShouldBe(26);

        var step = run.GetStep("notify");
        step.Type.ShouldBe(StepType.SendEvent);
        step.Output!.Value.GetProperty("ids")[0].GetString().ShouldBe(run.GetOutput<string[]>()![0]);
    }

    [Fact]
    public async Task Events_sent_outside_steps_are_sent_again_on_every_replay()
    {
        await using var host = For(async context =>
        {
            await context.Services.GetRequiredService<IInngestClient>().SendAsync(new InngestEvent("audit/replayed"), context.CancellationToken);
            await context.Step.RunAsync("a", () => 1);
            return null;
        });

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.SentEvents.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Middleware_transforms_events_before_the_host_records_them()
    {
        await using var host = For(
            async context =>
            {
                await context.Step.SendEventAsync("notify", new InngestEvent("user/notified"));
                return null;
            },
            configure: inngest => inngest.AddMiddleware(new StampVersion()));

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.SentEvents.ShouldHaveSingleItem().Version.ShouldBe("2026-10");
    }

    [Fact]
    public async Task An_event_the_client_would_refuse_fails_the_send_step()
    {
        await using var host = For(
            async context =>
            {
                await context.Step.SendEventAsync("notify", new InngestEvent("inngest/forged"));
                return null;
            },
            retries: 0);

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Failed);
        run.GetStep("notify").Error.ShouldBeOfType<ArgumentException>().Message.ShouldContain("reserved");
        run.SentEvents.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_invoke_returns_the_mocked_result()
    {
        await using var host = For(async context =>
            await context.Step.InvokeAsync<int>("tax", new InvokeOptions("tax-calculator") { Data = new { amount = 100 } }));

        var run = await host.RunAsync("fn", Event(), new StepMocks().Invoke("tax", 21), Cancel);

        run.GetOutput<int>().ShouldBe(21);
        var step = run.GetStep("tax");
        step.Type.ShouldBe(StepType.Invoke);
        step.Mocked.ShouldBeTrue();
        step.Attempts.ShouldBe(0);
        step.Options.GetProperty("function_id").GetString().ShouldBe("test-app-tax-calculator");
        step.Options.GetProperty("payload").GetProperty("data").GetProperty("amount").GetInt32().ShouldBe(100);
    }

    [Fact]
    public async Task A_failed_invoke_throws_a_step_failure_where_it_is_awaited()
    {
        await using var host = For(async context =>
        {
            try
            {
                return await context.Step.InvokeAsync<int>("tax", new InvokeOptions("tax-calculator"));
            }
            catch (StepFailedException failure)
            {
                return failure.Error.Name + ": " + failure.Error.Message;
            }
        });

        var run = await host.RunAsync("fn", Event(), new StepMocks().InvokeThrows("tax", new TimeoutException("tax service down")), Cancel);

        run.GetOutput<string>().ShouldBe("TimeoutException: tax service down");
        var step = run.GetStep("tax");
        step.Status.ShouldBe(TestStepStatus.Failed);
        step.Error.ShouldBeOfType<TimeoutException>();
    }

    [Fact]
    public async Task An_invoke_without_a_mock_leaves_the_run_waiting()
    {
        await using var host = For(async context => await context.Step.InvokeAsync<int>("tax", new InvokeOptions("tax-calculator")));

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Waiting);
        run.WaitingOn.ShouldHaveSingleItem().Id.ShouldBe("tax");
        run.ToString().ShouldBe("Waiting on step \"tax\" (InvokeFunction).");
    }

    [Fact]
    public async Task A_repeated_id_is_mocked_with_its_suffix()
    {
        await using var host = For(async context =>
        {
            var total = 0;
            for (var i = 1; i <= 3; i++)
            {
                var page = i;
                total += await context.Step.RunAsync("page", () => page);
            }

            return total;
        });

        var run = await host.RunAsync("fn", Event(), new StepMocks().Run("page:1", 20), Cancel);

        run.GetOutput<int>().ShouldBe(24);
        run.Steps.Select(step => (step.Id, step.Mocked)).ShouldBe([("page", false), ("page:1", true), ("page:2", false)]);
    }

    [Fact]
    public async Task Concurrent_runs_keep_their_own_events()
    {
        await using var host = For(async context =>
        {
            var id = context.Event.Data.GetProperty("id").GetInt32();
            await context.Step.RunAsync("work", async () =>
            {
                await Task.Yield();
                return id;
            });
            await context.Step.SendEventAsync("done", new InngestEvent("work/done", new { id }));
            return id;
        });

        var runs = await Task.WhenAll(Enumerable.Range(1, 8).Select(id => host.RunAsync("fn", Event(new { id }), Cancel)));

        foreach (var run in runs)
        {
            var id = run.GetOutput<int>();
            JsonSerializer.SerializeToElement(run.SentEvents.ShouldHaveSingleItem().Data).GetProperty("id").GetInt32().ShouldBe(id);
        }

        host.SentEvents.Count.ShouldBe(8);
    }

    [Fact]
    public async Task An_app_in_cloud_mode_without_an_event_key_still_sends()
    {
        var services = new ServiceCollection();
        services.AddInngest(options =>
        {
            options.AppId = AppId;
            options.IsDev = false;
        }).AddFunction(Options(), async (context, _) => await context.Step.SendEventAsync("notify", new InngestEvent("user/notified")));
        await using var host = new InngestTestHost(services);

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        run.SentEvents.ShouldHaveSingleItem().Name.ShouldBe("user/notified");
    }

    [Fact]
    public async Task Requests_through_the_sdks_http_client_never_leave_the_process()
    {
        await using var host = For(_ => Task.FromResult<object?>(null));
        using var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("Inngest");

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => client.GetAsync(new Uri("http://localhost:8288/v0/runs/01J/actions"), Cancel));

        exception.Message.ShouldBe("The test host keeps runs in memory and does not send GET http://localhost:8288/v0/runs/01J/actions to Inngest.");
    }

    [Fact]
    public async Task An_unknown_function_id_names_the_registered_ones()
    {
        await using var host = For(_ => Task.FromResult<object?>(null));

        var exception = await Should.ThrowAsync<ArgumentException>(() => host.RunAsync("missing", Event(), Cancel));

        exception.Message.ShouldContain("\"fn\"");
    }

    [Fact]
    public void Function_options_inngest_would_reject_fail_when_the_host_starts()
    {
        var services = new ServiceCollection();
        services.AddInngest(options => options.AppId = AppId).AddFunction(Options(retries: 99), (_, _) => Task.FromResult<object?>(null));

        Should.Throw<InvalidOperationException>(() => new InngestTestHost(services)).Message.ShouldContain("Retries must be between 0 and 20");
    }

    [Fact]
    public void Services_without_AddInngest_are_refused()
    {
        Should.Throw<InvalidOperationException>(() => new InngestTestHost(new ServiceCollection())).Message.ShouldContain("AddInngest");
    }

    [Fact]
    public async Task A_canceled_run_throws()
    {
        await using var host = For(_ => Task.FromResult<object?>(null));

        await Should.ThrowAsync<OperationCanceledException>(() => host.RunAsync("fn", Event(), new CancellationToken(canceled: true)));
    }

    private static string Hash(string id) => Inngest.Execution.StepIds.Hash(id);

    public sealed record Order(string Id, decimal Total, DateTimeOffset PlacedAt);

    public sealed record SignedUp(string Name);

    public sealed record Greeting(string Text);

    public sealed record Payment(int Amount);

    public sealed record Notified(int UserId);

    public interface IGreetings
    {
        string Greet(string name);
    }

    public sealed class Greetings : IGreetings
    {
        public string Greet(string name) => $"Hello, {name}";
    }

    public sealed class Greeter(IGreetings greetings) : InngestFunction<SignedUp>
    {
        public override async Task<object?> ExecuteAsync(FunctionContext<SignedUp> context, CancellationToken cancellationToken)
        {
            var text = await context.Step.RunAsync("greet", () => greetings.Greet(context.Event.Data.Name));
            return new Greeting(text);
        }
    }

    public sealed class HookLog
    {
        public List<string> Entries { get; } = [];
    }

    public sealed class Hooks(HookLog log) : InngestMiddleware
    {
        public override ValueTask OnRunStartAsync(FunctionContext context)
        {
            log.Entries.Add("run start");
            return ValueTask.CompletedTask;
        }

        public override ValueTask OnStepStartAsync(FunctionContext context, StepInfo step)
        {
            log.Entries.Add($"step start {step.Id}");
            return ValueTask.CompletedTask;
        }

        public override ValueTask OnStepCompleteAsync(FunctionContext context, StepInfo step, object? output)
        {
            log.Entries.Add($"step complete {step.Id}");
            return ValueTask.CompletedTask;
        }

        public override ValueTask OnRunCompleteAsync(FunctionContext context, object? output)
        {
            log.Entries.Add($"run complete {output}");
            return ValueTask.CompletedTask;
        }

        public override async Task<object?> WrapStepHandlerAsync(FunctionContext context, StepInfo step, Func<Task<object?>> next)
        {
            var output = await next();
            log.Entries.Add($"wrap handler {step.Id} -> {output}");
            return output;
        }
    }

    private sealed class StampVersion : InngestMiddleware
    {
        public override Task<SendEventsResult> WrapSendEventAsync(
            FunctionContext? context,
            IReadOnlyList<InngestEvent> events,
            Func<IReadOnlyList<InngestEvent>, Task<SendEventsResult>> next,
            CancellationToken cancellationToken) =>
            next([.. events.Select(@event => @event with { Version = "2026-10" })]);
    }
}
