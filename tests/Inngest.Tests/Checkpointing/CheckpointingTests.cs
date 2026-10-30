using System.Text.Json;
using System.Text.Json.Serialization;
using Inngest.Execution;
using Inngest.Functions;
using Inngest.Tests.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Inngest.Tests.Checkpointing.Checkpointed;

namespace Inngest.Tests.Checkpointing;

// A discovery call of a function with checkpointing runs one step after another, saves each to Inngest and answers
// only when the run completes or needs Inngest: at a sleep, a wait, an invoke, parallel steps or an error.
public sealed class CheckpointingTests : IDisposable
{
    private readonly EngineHost _host = new();
    private readonly List<string> _timeline = [];

    public CheckpointingTests() => _host.Checkpoints.Timeline = _timeline;

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Consecutive_steps_run_in_one_call_and_each_is_saved_before_the_next_runs()
    {
        var result = await _host.CallCheckpointedAsync(Sequence("A", "B"), Call.New());

        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe("\"AB\"");
        _timeline.ShouldBe(["run A", "checkpoint A", "run B", "checkpoint B"]);
        _host.Checkpoints.Checkpoints.Select(checkpoint => checkpoint.Steps).ShouldBe([$"[{StepRun("A", "\"A\"")}]", $"[{StepRun("B", "\"B\"")}]"]);
    }

    [Fact]
    public async Task A_step_result_as_deep_as_the_serializer_reads_is_checkpointed()
    {
        // It sits one level deeper in the op that is buffered.
        var deep = new string('[', 64) + "1" + new string(']', 64);
        var function = Define(async context =>
        {
            using var document = JsonDocument.Parse(deep);
            var output = await context.Step.RunAsync("A", () => document.RootElement.Clone());
            return output.GetRawText() == deep;
        });

        // A host without a timeline: the timeline reads the saved steps back at the default depth.
        using var host = new EngineHost();
        var result = await host.CallCheckpointedAsync(function, Call.New());

        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe("true");
        host.Checkpoints.Checkpoints.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_checkpoint_names_the_run_the_function_the_queue_item_and_the_call()
    {
        await _host.CallCheckpointedAsync(Sequence("A"), Call.New());

        _host.Checkpoints.Checkpoints.ShouldHaveSingleItem().ShouldBe(
            new Checkpoint("01JRUN", FunctionUuid, QueueItemId, RequestId, GenerationId, EngineHost.Now, $"[{StepRun("A", "\"A\"")}]"));
    }

    [Fact]
    public async Task Ids_that_Inngest_did_not_send_are_left_out_of_the_checkpoint()
    {
        var request = Request(_host, Sequence("A"), Call.New()) with { RequestId = null, GenerationId = null };

        await EngineHost.ResultOf(_host.Start(request));

        var checkpoint = _host.Checkpoints.Checkpoints.ShouldHaveSingleItem();
        checkpoint.RequestId.ShouldBeNull();
        checkpoint.GenerationId.ShouldBeNull();
    }

    [Fact]
    public async Task Memoized_steps_are_replayed_and_only_new_steps_are_saved()
    {
        var result = await _host.CallCheckpointedAsync(Sequence("A", "B", "C"), Call.New().Data("A", "\"A\""));

        result.Body.ShouldBe("\"ABC\"");
        _timeline.ShouldBe(["run B", "checkpoint B", "run C", "checkpoint C"]);
    }

    [Fact]
    public async Task A_step_id_used_in_a_loop_gets_a_numbered_id_for_every_iteration()
    {
        var function = Define(async context =>
        {
            var squares = new List<int>();
            for (var i = 0; i < 3; i++)
            {
                var value = i;
                squares.Add(await context.Step.RunAsync("square", () => value * value));
            }

            return squares;
        });

        var result = await _host.CallCheckpointedAsync(function, Call.New());

        result.Body.ShouldBe("[0,1,4]");
        _host.Checkpoints.Checkpoints.Select(checkpoint => checkpoint.Ops[0].GetProperty("id").GetString())
            .ShouldBe([Call.Hash("square"), Call.Hash("square:1"), Call.Hash("square:2")]);
    }

    [Fact]
    public async Task A_call_that_is_cancelled_while_saving_ends_without_an_answer()
    {
        var release = _host.Checkpoints.Hold();
        using var cancellation = new CancellationTokenSource();
        var call = _host.Engine.ExecuteAsync(Request(_host, Sequence("A", "B"), Call.New()), cancellation.Token);

        await _host.Checkpoints.Saved(1);
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(call);
        release.TrySetResult();
        _timeline.ShouldBe(["run A", "checkpoint A"]);
    }

    [Fact]
    public async Task The_function_gets_the_result_back_through_JSON_as_a_replay_would()
    {
        var function = Define(async context =>
        {
            Box? original = null;
            var box = await context.Step.RunAsync("A", () => original = new Box(1) { Note = "not serialized" });
            return new BoxSeen(ReferenceEquals(box, original), box.Value, box.Note);
        });

        var result = await _host.CallCheckpointedAsync(function, Call.New());

        result.Body.ShouldBe("""{"same":false,"value":1,"note":null}""");
        _host.Checkpoints.Checkpoints.ShouldHaveSingleItem().Ops[0].GetProperty("data").GetRawText().ShouldBe("""{"value":1}""");
    }

    [Fact]
    public async Task A_step_without_output_is_saved_with_null_data()
    {
        var function = Define(async context =>
        {
            await context.Step.RunAsync("A", () => { });
            return "done";
        });

        var result = await _host.CallCheckpointedAsync(function, Call.New());

        result.Body.ShouldBe("\"done\"");
        _host.Checkpoints.Checkpoints.ShouldHaveSingleItem().Steps.ShouldBe($"[{StepRun("A", "null")}]");
    }

    [Fact]
    public async Task Steps_are_saved_together_once_BufferedSteps_have_run_and_the_rest_before_the_answer()
    {
        var result = await _host.CallCheckpointedAsync(Sequence(new CheckpointingOptions { BufferedSteps = 2 }, "A", "B", "C"), Call.New());

        result.StatusCode.ShouldBe(200);
        _timeline.ShouldBe(["run A", "run B", "checkpoint A,B", "run C", "checkpoint C"]);
        _host.Checkpoints.Checkpoints[0].Steps.ShouldBe($"[{StepRun("A", "\"A\"")},{StepRun("B", "\"B\"")}]");
    }

    [Theory]
    [InlineData("sleep", "Sleep")]
    [InlineData("wait", "WaitForEvent")]
    [InlineData("invoke", "InvokeFunction")]
    [InlineData("parallel", "StepPlanned,StepPlanned")]
    public async Task The_call_answers_Inngest_once_the_function_needs_it_and_saves_the_steps_it_ran_first(string next, string ops)
    {
        var function = Define(async context =>
        {
            await Run(context, "A");
            switch (next)
            {
                case "sleep":
                    await context.Step.SleepAsync("nap", TimeSpan.FromMinutes(1));
                    break;
                case "wait":
                    await context.Step.WaitForEventAsync("approval", new WaitForEventOptions("test/approved", TimeSpan.FromHours(1)));
                    break;
                case "invoke":
                    await context.Step.InvokeAsync<JsonElement>("child", new InvokeOptions("other-fn"));
                    break;
                default:
                    await Task.WhenAll(Run(context, "B"), Run(context, "C"));
                    break;
            }

            await Run(context, "after");
            return null;
        }, new CheckpointingOptions { BufferedSteps = 5 });

        var result = await _host.CallCheckpointedAsync(function, Call.New());

        result.StatusCode.ShouldBe(206);
        string.Join(",", result.OpCodes).ShouldBe(ops);
        _timeline.ShouldBe(["run A", "checkpoint A"]);
    }

    [Fact]
    public async Task A_step_error_is_answered_after_the_steps_before_it_are_saved()
    {
        var function = Define(async context =>
        {
            await Run(context, "A");
            await context.Step.RunAsync<string>("B", _ => throw new InvalidOperationException("card declined"));
            return null;
        }, new CheckpointingOptions { BufferedSteps = 5 });

        var result = await _host.CallCheckpointedAsync(function, Call.New());

        result.StatusCode.ShouldBe(206);
        result.OpCodes.ShouldBe(["StepError"]);
        result.NoRetry.ShouldBe(false);
        result.Op().GetProperty("error").GetProperty("message").GetString().ShouldBe("card declined");
        _timeline.ShouldBe(["run A", "checkpoint A"]);
    }

    [Fact]
    public async Task A_function_error_is_answered_after_the_steps_before_it_are_saved()
    {
        var function = Define(async context =>
        {
            await Run(context, "A");
            throw new InvalidOperationException("broken");
        }, new CheckpointingOptions { BufferedSteps = 5 });

        var result = await _host.CallCheckpointedAsync(function, Call.New());

        result.StatusCode.ShouldBe(500);
        result.Json.GetProperty("message").GetString().ShouldBe("broken");
        _timeline.ShouldBe(["run A", "checkpoint A"]);
    }

    [Fact]
    public async Task A_step_whose_output_cannot_be_serialized_fails_as_without_checkpointing()
    {
        var function = Define(async context =>
        {
            await context.Step.RunAsync("A", () => new Unwritable());
            return null;
        });

        var result = await _host.CallCheckpointedAsync(function, Call.New());

        result.OpCodes.ShouldBe(["StepError"]);
        _host.Checkpoints.Checkpoints.ShouldBeEmpty();
    }

    [Fact]
    public async Task Past_MaxRuntime_the_next_step_is_left_to_Inngest()
    {
        var function = Define(async context =>
        {
            await context.Step.RunAsync("A", () =>
            {
                _host.Time.Advance(TimeSpan.FromSeconds(10));
                return "A";
            });
            await Run(context, "B");
            return null;
        });

        var result = await _host.CallCheckpointedAsync(function, Call.New());

        result.StatusCode.ShouldBe(206);
        result.Body.ShouldBe($$$"""[{"id":"{{{Call.Hash("B")}}}","op":"StepPlanned","name":"B","displayName":"B","opts":{"type":"step.run"},"userland":{"id":"B"}}]""");
        _host.Checkpoints.Checkpoints.ShouldHaveSingleItem().Steps.ShouldBe($"[{StepRun("A", "\"A\"", durationNanoseconds: 10_000_000_000)}]");
    }

    [Theory]
    [InlineData(30)]
    [InlineData(-1)]
    public async Task MaxRuntime_can_be_longer_or_unlimited(int seconds)
    {
        var maxRuntime = seconds < 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(seconds);
        var function = Define(async context =>
        {
            await context.Step.RunAsync("A", () => _host.Time.Advance(TimeSpan.FromSeconds(20)));
            await Run(context, "B");
            return "done";
        }, new CheckpointingOptions { MaxRuntime = maxRuntime });

        var result = await _host.CallCheckpointedAsync(function, Call.New());

        result.StatusCode.ShouldBe(200);
        _host.Checkpoints.Checkpoints.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_Connect_call_runs_steps_for_up_to_five_minutes_by_default()
    {
        var function = Define(async context =>
        {
            await context.Step.RunAsync("A", () => _host.Time.Advance(TimeSpan.FromMinutes(4)));
            await Run(context, "B");
            return "done";
        });
        var request = Request(_host, function, Call.New()) with { DefaultCheckpointMaxRuntime = Checkpointer.ConnectMaxRuntime };

        var result = await EngineHost.ResultOf(_host.Start(request));

        result.StatusCode.ShouldBe(200);
        _host.Checkpoints.Checkpoints.Count.ShouldBe(2);
    }

    [Fact]
    public async Task MaxInterval_saves_buffered_steps_while_the_function_waits_on_its_own_work()
    {
        var function = Define(async context =>
        {
            await Run(context, "A");
            await Task.Delay(TimeSpan.FromSeconds(5), _host.Time);
            await Run(context, "B");
            return "done";
        }, new CheckpointingOptions { BufferedSteps = 10, MaxInterval = TimeSpan.FromSeconds(1) });

        var call = _host.Start(Request(_host, function, Call.New()));

        // The function's own delay, then the engine's wait for the interval.
        await _host.Time.TimersCreated(2);
        _timeline.ShouldBe(["run A"]);
        _host.Time.Advance(TimeSpan.FromSeconds(1));
        await _host.Checkpoints.Saved(1);
        _timeline.ShouldBe(["run A", "checkpoint A"]);

        _host.Time.Advance(TimeSpan.FromSeconds(4));
        var result = await EngineHost.ResultOf(call);

        result.StatusCode.ShouldBe(200);
        _timeline.ShouldBe(["run A", "checkpoint A", "run B", "checkpoint B"]);
    }

    [Fact]
    public async Task MaxInterval_saves_steps_that_waited_while_a_long_step_ran()
    {
        var function = Define(async context =>
        {
            await Run(context, "A");
            await context.Step.RunAsync("slow", () => _host.Time.Advance(TimeSpan.FromSeconds(2)));
            await Run(context, "C");
            return "done";
        }, new CheckpointingOptions { BufferedSteps = 10, MaxInterval = TimeSpan.FromSeconds(1) });

        await _host.CallCheckpointedAsync(function, Call.New());

        _host.Checkpoints.Checkpoints.Select(checkpoint => string.Join(",", checkpoint.DisplayNames)).ShouldBe(["A,slow", "C"]);
    }

    [Fact]
    public async Task When_Inngest_cannot_save_the_steps_the_call_answers_with_them_instead()
    {
        _host.Checkpoints.Fail();

        var result = await _host.CallCheckpointedAsync(Sequence(new CheckpointingOptions { BufferedSteps = 2 }, "A", "B", "C"), Call.New());

        result.StatusCode.ShouldBe(206);
        result.NoRetry.ShouldBeNull();
        result.Body.ShouldBe($"[{StepRun("A", "\"A\"")},{StepRun("B", "\"B\"")}]");
        _timeline.ShouldBe(["run A", "run B", "checkpoint A,B"]);
        var warning = _host.Logs.GetSnapshot().Where(record => record.Id.Id == 141).ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Exception.ShouldBeOfType<InngestException>();
    }

    [Fact]
    public async Task Any_failure_to_save_falls_back_to_answering_with_the_steps()
    {
        _host.Checkpoints.Throw(new HttpRequestException("connection refused"));

        var result = await _host.CallCheckpointedAsync(Sequence("A", "B"), Call.New());

        result.StatusCode.ShouldBe(206);
        result.Body.ShouldBe($"[{StepRun("A", "\"A\"")}]");
    }

    [Fact]
    public async Task Steps_still_buffered_at_the_end_are_answered_when_they_cannot_be_saved()
    {
        var function = Sequence(new CheckpointingOptions { BufferedSteps = 5 }, "A");
        _host.Checkpoints.Fail();

        var result = await _host.CallCheckpointedAsync(function, Call.New());

        result.StatusCode.ShouldBe(206);
        result.Body.ShouldBe($"[{StepRun("A", "\"A\"")}]");
    }

    [Fact]
    public async Task A_stale_dispatch_stops_the_call_without_a_retry()
    {
        _host.Checkpoints.Stale();

        var result = await _host.CallCheckpointedAsync(Sequence("A", "B"), Call.New());

        result.StatusCode.ShouldBe(400);
        result.NoRetry.ShouldBe(true);
        result.Json.GetProperty("name").GetString().ShouldBe(nameof(InngestException));
        _timeline.ShouldBe(["run A", "checkpoint A"]);
        _host.Checkpoints.Checkpoints.Count.ShouldBe(1);
        _host.Logs.GetSnapshot().Where(record => record.Id.Id == 142).ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Warning);
    }

    [Fact]
    public async Task A_retried_call_answers_after_its_first_successful_step()
    {
        var result = await _host.CallCheckpointedAsync(Sequence("A", "B"), Call.New().Attempt(1));

        result.StatusCode.ShouldBe(206);
        result.Body.ShouldBe($"[{StepRun("A", "\"A\"")}]");
        _timeline.ShouldBe(["run A"]);
    }

    [Fact]
    public async Task A_retried_call_reports_a_step_error_as_without_checkpointing()
    {
        var function = Define(async context =>
        {
            await context.Step.RunAsync<string>("A", _ => throw new InvalidOperationException("again"));
            return null;
        });

        var result = await _host.CallCheckpointedAsync(function, Call.New().Attempt(1));

        result.OpCodes.ShouldBe(["StepError"]);
        _host.Checkpoints.Checkpoints.ShouldBeEmpty();
    }

    [Fact]
    public async Task Calls_that_cannot_checkpoint_answer_after_one_step()
    {
        var function = Sequence("A", "B");
        var single = $"[{StepRun("A", "\"A\"")}]";

        (await Answer(Request(_host, function, Call.New()) with { Body = Body(Call.New(), queueItemId: null) })).Body.ShouldBe(single);
        (await Answer(Request(_host, function, Call.New(), stepId: Call.Hash("A")))).Body.ShouldBe(single);
        (await Answer(Request(_host, function, Call.New().DisableImmediateExecution()))).OpCodes.ShouldBe(["StepPlanned"]);
        (await Answer(Request(_host, Sequence(new CheckpointingOptions { Enabled = false }, "A", "B"), Call.New()))).Body.ShouldBe(single);
        (await Answer(Request(_host, TestFunction.Define(async context => await Run(context, "A")), Call.New()))).Body.ShouldBe(single);

        _host.Checkpoints.Checkpoints.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_app_setting_applies_to_functions_without_their_own_and_theirs_wins()
    {
        var settings = EngineHost.Settings with { Checkpointing = new CheckpointingOptions() };
        var engine = new ExecutionEngine(settings, _host.Services.GetRequiredService<ILoggerFactory>(), _host.Time, _host.Api, _host.Checkpoints);

        var inherited = await Answer(engine, Request(_host, TestFunction.Define(async context => await Run(context, "A") + await Run(context, "B")), Call.New()));
        var optedOut = await Answer(engine, Request(_host, Sequence(new CheckpointingOptions { Enabled = false }, "A", "B"), Call.New()));
        var buffered = await Answer(engine, Request(_host, Sequence(new CheckpointingOptions { BufferedSteps = 3 }, "A", "B"), Call.New()));

        inherited.Body.ShouldBe("\"AB\"");
        optedOut.Body.ShouldBe($"[{StepRun("A", "\"A\"")}]");
        buffered.Body.ShouldBe("\"AB\"");
        _host.Checkpoints.Checkpoints.Select(checkpoint => string.Join(",", checkpoint.DisplayNames)).ShouldBe(["A", "B", "A,B"]);
    }

    [Fact]
    public async Task A_loop_whose_steps_all_run_in_one_call_is_not_taken_for_a_reused_id()
    {
        const int StepIdReusedEventId = 101;
        var function = Define(async context =>
        {
            var total = 0;
            for (var i = 1; i <= 3; i++)
            {
                var n = i;
                total += await context.Step.RunAsync("square", () => n * n);
            }

            return total;
        }, new CheckpointingOptions { BufferedSteps = 10 });

        var result = await _host.CallCheckpointedAsync(function, Call.New());

        result.Body.ShouldBe("14");
        _host.Logs.GetSnapshot().ShouldNotContain(record => record.Id.Id == StepIdReusedEventId);
    }

    [Fact]
    public async Task A_MaxInterval_longer_than_a_timer_can_wait_does_not_fail_the_call()
    {
        var function = Define(async context =>
        {
            var a = await context.Step.RunAsync("A", () => "A");
            await Task.Delay(5);
            return a + await context.Step.RunAsync("B", () => "B");
        }, new CheckpointingOptions { BufferedSteps = 10, MaxInterval = TimeSpan.FromDays(60) });

        var result = await _host.CallCheckpointedAsync(function, Call.New());

        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe("\"AB\"");
    }

    private Task<string> Run(FunctionContext context, string id) => context.Step.RunAsync(id, () =>
    {
        _timeline.Add($"run {id}");
        return id;
    });

    private FunctionDefinition Sequence(params string[] ids) => Sequence(new CheckpointingOptions(), ids);

    private FunctionDefinition Sequence(CheckpointingOptions options, params string[] ids) => Define(async context =>
    {
        var output = string.Empty;
        foreach (var id in ids)
        {
            output += await Run(context, id);
        }

        return output;
    }, options);

    private Task<CallResult> Answer(ExecutionRequest request) => Answer(_host.Engine, request);

    private static Task<CallResult> Answer(ExecutionEngine engine, ExecutionRequest request) =>
        EngineHost.ResultOf(engine.ExecuteAsync(request, TestContext.Current.CancellationToken));

    public sealed record Box(int Value)
    {
        [JsonIgnore]
        public string? Note { get; init; }
    }

    public sealed record BoxSeen(bool Same, int Value, string? Note);

    public sealed class Unwritable
    {
        public string Value => throw new InvalidOperationException("cannot be read");
    }
}
