using System.Text.Json;
using Inngest.Functions;

namespace Inngest.Tests.Execution;

public sealed class ExecutionEngineTests : IDisposable
{
    private const string HashA = "6dcd4ce23d88e2ee9568ba546c007c63d9131c1b";
    private const string HashB = "ae4f281df5a5d0ff3cad6371f76d5c29b6d953ec";

    private readonly EngineHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task First_call_executes_the_first_step_right_away()
    {
        var ran = new List<string>();
        var function = SequentialAToB(ran);

        var result = await _host.CallAsync(function, Call.New());

        result.StatusCode.ShouldBe(206);
        result.NoRetry.ShouldBeNull();
        result.Body.ShouldBe(
            $$$"""[{"id":"{{{HashA}}}","op":"StepRun","name":"A","displayName":"A","opts":{"type":"step.run"},"userland":{"id":"A"},"data":"A","timing":{"a":{{{EngineHost.NowNanoseconds}}},"b":0}}]""");
        ran.ShouldBe(["A"]);
    }

    [Fact]
    public async Task Replay_hands_back_memoized_results_and_executes_the_next_step()
    {
        var ran = new List<string>();
        var function = SequentialAToB(ran);

        var result = await _host.CallAsync(function, Call.New().Data("A", "\"A\""));

        result.StatusCode.ShouldBe(206);
        result.Op().GetProperty("id").GetString().ShouldBe(HashB);
        result.Op().GetProperty("op").GetString().ShouldBe("StepRun");
        result.Op().GetProperty("data").GetString().ShouldBe("B");
        ran.ShouldBe(["B"]);
    }

    [Fact]
    public async Task Final_call_returns_the_output()
    {
        var ran = new List<string>();
        var function = SequentialAToB(ran);

        var result = await _host.CallAsync(function, Call.New().Data("A", "\"A\"").Data("B", "\"B\""));

        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe("\"AB\"");
        result.NoRetry.ShouldBeNull();
        ran.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_run_completes_across_calls_that_feed_back_each_result()
    {
        var ran = new List<string>();
        var function = SequentialAToB(ran);
        var call = Call.New();

        var first = await _host.CallAsync(function, call);
        call.Data("A", first.Op().GetProperty("data").GetRawText());
        var second = await _host.CallAsync(function, call);
        call.Data("B", second.Op().GetProperty("data").GetRawText());
        var third = await _host.CallAsync(function, call);

        third.StatusCode.ShouldBe(200);
        third.Body.ShouldBe("\"AB\"");
        ran.ShouldBe(["A", "B"]);
    }

    [Fact]
    public async Task Memoized_values_are_deserialized_into_the_declared_type()
    {
        var function = TestFunction.Define(async context =>
        {
            var order = await context.Step.RunAsync("load", () => new Order("o-1", 42.5m, ["a", "b"]));
            return order.Total * 2;
        });

        var result = await _host.CallAsync(function, Call.New().Data("load", """{"id":"o-1","total":42.5,"items":["a","b"]}"""));

        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe("85.0");
    }

    [Fact]
    public async Task Step_output_is_serialized_as_the_declared_type()
    {
        var function = TestFunction.Define(async context =>
        {
            await context.Step.RunAsync<Order>("load", () => new Order("o-1", 1, []));
            return null;
        });

        var result = await _host.CallAsync(function, Call.New());

        result.Op().GetProperty("data").GetRawText().ShouldBe("""{"id":"o-1","total":1,"items":[]}""");
    }

    [Fact]
    public async Task Steps_without_a_value_store_null()
    {
        var function = TestFunction.Define(async context =>
        {
            await context.Step.RunAsync("side-effect", () => { });
            return null;
        });

        var result = await _host.CallAsync(function, Call.New());

        result.Op().GetProperty("data").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Theory]
    [InlineData(null, "null")]
    [InlineData(42, "42")]
    [InlineData("done", "\"done\"")]
    [InlineData(true, "true")]
    public async Task Output_is_serialized_as_json(object? output, string expected)
    {
        var function = TestFunction.Define(_ => Task.FromResult(output));

        var result = await _host.CallAsync(function, Call.New());

        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe(expected);
    }

    [Fact]
    public async Task Record_output_uses_the_serializer_options()
    {
        var function = TestFunction.Define(_ => Task.FromResult<object?>(new Order("o-2", 3, ["x"])));

        var result = await _host.CallAsync(function, Call.New());

        result.Body.ShouldBe("""{"id":"o-2","total":3,"items":["x"]}""");
    }

    [Fact]
    public async Task Output_that_cannot_be_serialized_fails_the_run()
    {
        var function = TestFunction.Define(_ => Task.FromResult<object?>(new Unserializable()));

        var result = await _host.CallAsync(function, Call.New());

        result.StatusCode.ShouldBe(500);
        result.NoRetry.ShouldBe(false);
    }

    [Fact]
    public async Task The_function_receives_the_event_and_run_details()
    {
        FunctionContext<Payload>? seen = null;
        var function = TestFunction.Define<Payload>(context =>
        {
            seen = context;
            return Task.FromResult<object?>(null);
        });

        await _host.CallAsync(function, Call.New().Attempt(2, 5));

        seen.ShouldNotBeNull();
        seen.FunctionId.ShouldBe("fn");
        seen.RunId.ShouldBe("01JRUN");
        seen.Attempt.ShouldBe(2);
        seen.MaxAttempts.ShouldBe(5);
        seen.Event.Name.ShouldBe("test/event");
        seen.Event.Data.Value.ShouldBe(1);
        seen.Events.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_batch_hands_every_event_to_the_function()
    {
        IReadOnlyList<InngestEvent<Payload>>? events = null;
        var function = TestFunction.Define<Payload>(context =>
        {
            events = context.Events;
            return Task.FromResult<object?>(null);
        });

        await _host.CallAsync(function, Call.New().Events(
            """{"data":{"value":1},"name":"test/event"}""",
            """{"data":{"value":2},"name":"test/event"}"""));

        events.ShouldNotBeNull();
        events.Select(e => e.Data.Value).ShouldBe([1, 2]);
    }

    [Fact]
    public async Task A_failure_handler_runs_with_the_failure_and_can_use_steps()
    {
        FunctionFailure? failure = null;
        var function = new FunctionDefinition(
            FailureFunction.CreateOptions(new FunctionOptions { Id = "charge", Triggers = [] }),
            _ => new FailureFunction(async (context, _) =>
            {
                failure = context.Event.Data;
                await context.Step.RunAsync("notify", () => "notified");
            }))
        { FailureHandlerFor = "charge" };
        var call = Call.New().Event(
            """{"data":{"error":{"message":"card declined","name":"InvalidOperationException","stack":"at Charge()"},"event":{"data":{},"name":"shop/order.placed"},"function_id":"test-app-charge","run_id":"01JFAILED"},"name":"inngest/function.failed"}""");

        var first = await _host.CallAsync(function, call);
        var second = await _host.CallAsync(function, call.Data("notify", "\"notified\""));

        first.DisplayNames.ShouldBe(["notify"]);
        second.StatusCode.ShouldBe(200);
        second.Body.ShouldBe("null");
        failure.ShouldNotBeNull();
        failure.FunctionId.ShouldBe("test-app-charge");
        failure.RunId.ShouldBe("01JFAILED");
        failure.Error.ShouldBe(new FunctionError { Name = "InvalidOperationException", Message = "card declined", Stack = "at Charge()" });
        failure.Event.GetProperty("name").GetString().ShouldBe("shop/order.placed");
    }

    [Fact]
    public async Task A_thrown_exception_is_retried()
    {
        var function = TestFunction.Define(_ => throw new InvalidOperationException("boom"));

        var result = await _host.CallAsync(function, Call.New());

        result.StatusCode.ShouldBe(500);
        result.NoRetry.ShouldBe(false);
        result.RetryAfter.ShouldBeNull();
        result.Json.GetProperty("name").GetString().ShouldBe("InvalidOperationException");
        result.Json.GetProperty("message").GetString().ShouldBe("boom");
        result.Json.GetProperty("stack").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task An_exception_thrown_after_an_await_is_retried()
    {
        var function = TestFunction.Define(async _ =>
        {
            await Task.Yield();
            throw new InvalidOperationException("later");
        });

        var result = await _host.CallAsync(function, Call.New());

        result.StatusCode.ShouldBe(500);
        result.Json.GetProperty("message").GetString().ShouldBe("later");
    }

    [Fact]
    public async Task Inner_exceptions_become_the_cause()
    {
        var function = TestFunction.Define(_ =>
            throw new InvalidOperationException("outer", new ArgumentException("inner", new TimeoutException("innermost"))));

        var result = await _host.CallAsync(function, Call.New());

        var cause = result.Json.GetProperty("cause");
        cause.GetProperty("name").GetString().ShouldBe("ArgumentException");
        cause.GetProperty("message").GetString().ShouldBe("inner");
        cause.GetProperty("cause").GetProperty("name").GetString().ShouldBe("TimeoutException");
    }

    [Fact]
    public async Task A_non_retriable_exception_fails_the_run_for_good()
    {
        var function = TestFunction.Define(_ => throw new NonRetriableException("bad input"));

        var result = await _host.CallAsync(function, Call.New());

        result.StatusCode.ShouldBe(400);
        result.NoRetry.ShouldBe(true);
        result.Json.GetProperty("name").GetString().ShouldBe("NonRetriableException");
        result.Json.GetProperty("message").GetString().ShouldBe("bad input");
    }

    [Fact]
    public async Task An_error_on_the_last_attempt_is_final()
    {
        var function = TestFunction.Define(_ => throw new InvalidOperationException("boom"));

        var result = await _host.CallAsync(function, Call.New().Attempt(3, maxAttempts: 4));

        result.StatusCode.ShouldBe(400);
        result.NoRetry.ShouldBe(true);
    }

    [Fact]
    public async Task An_error_before_the_last_attempt_is_retried()
    {
        var function = TestFunction.Define(_ => throw new InvalidOperationException("boom"));

        var result = await _host.CallAsync(function, Call.New().Attempt(2, maxAttempts: 4));

        result.StatusCode.ShouldBe(500);
        result.NoRetry.ShouldBe(false);
    }

    [Fact]
    public async Task Retry_after_a_delay_is_sent_in_whole_seconds()
    {
        var function = TestFunction.Define(_ => throw new RetryAfterException("rate limited", TimeSpan.FromSeconds(29.2)));

        var result = await _host.CallAsync(function, Call.New());

        result.StatusCode.ShouldBe(500);
        result.NoRetry.ShouldBe(false);
        result.RetryAfter.ShouldBe("30");
        result.Json.GetProperty("name").GetString().ShouldBe("RetryAfterException");
    }

    [Fact]
    public async Task Retry_at_a_time_is_sent_as_rfc_3339()
    {
        var at = new DateTimeOffset(2026, 10, 29, 12, 30, 0, TimeSpan.FromHours(2));
        var function = TestFunction.Define(_ => throw new RetryAfterException("later", at));

        var result = await _host.CallAsync(function, Call.New());

        result.RetryAfter.ShouldBe("2026-10-29T10:30:00.000Z");
    }

    [Fact]
    public async Task Retry_after_on_the_last_attempt_is_final()
    {
        var function = TestFunction.Define(_ => throw new RetryAfterException("rate limited", TimeSpan.FromSeconds(5)));

        var result = await _host.CallAsync(function, Call.New().Attempt(3, maxAttempts: 4));

        result.StatusCode.ShouldBe(400);
        result.NoRetry.ShouldBe(true);
        result.RetryAfter.ShouldBeNull();
    }

    [Fact]
    public async Task A_caught_memoized_failure_lets_the_run_continue()
    {
        var function = TestFunction.Define(async context =>
        {
            try
            {
                await context.Step.RunAsync("charge", () => "never runs");
                return "charged";
            }
            catch (StepFailedException failure)
            {
                return $"{failure.StepId}: {failure.Error.Name}: {failure.Message}";
            }
        });

        var result = await _host.CallAsync(function, Call.New().Failed("charge", "InvalidOperationException", "card declined"));

        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe("\"charge: InvalidOperationException: card declined\"");
    }

    [Fact]
    public async Task An_uncaught_memoized_failure_fails_the_run_with_the_step_error()
    {
        var function = TestFunction.Define(async context =>
        {
            await context.Step.RunAsync("charge", () => "never runs");
            return "charged";
        });

        var result = await _host.CallAsync(function, Call.New().Failed("charge", "InvalidOperationException", "card declined"));

        result.StatusCode.ShouldBe(400);
        result.NoRetry.ShouldBe(true);
        result.Body.ShouldBe("""{"name":"InvalidOperationException","message":"card declined","stack":"at Somewhere()"}""");
    }

    [Fact]
    public async Task A_memoized_failure_wrapped_by_the_function_is_retried()
    {
        var function = TestFunction.Define(async context =>
        {
            try
            {
                await context.Step.RunAsync("charge", () => "never runs");
            }
            catch (StepFailedException failure)
            {
                throw new InvalidOperationException("payment failed", failure);
            }

            return null;
        });

        var result = await _host.CallAsync(function, Call.New().Failed("charge", "Error", "card declined"));

        result.StatusCode.ShouldBe(500);
        result.Json.GetProperty("cause").GetProperty("message").GetString().ShouldBe("card declined");
    }

    [Fact]
    public async Task Memoized_data_that_looks_like_an_error_is_still_data()
    {
        var function = TestFunction.Define(async context =>
            (object?)await context.Step.RunAsync("error-shaped", () => new Shaped("Alice", "Hello")));

        var result = await _host.CallAsync(function, Call.New().Data("error-shaped", """{"name":"Alice","message":"Hello"}"""));

        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe("""{"name":"Alice","message":"Hello"}""");
    }

    [Fact]
    public async Task Memoized_data_of_the_wrong_shape_throws_at_the_call_site()
    {
        var function = TestFunction.Define(async context =>
        {
            try
            {
                await context.Step.RunAsync("count", () => 1);
                return "no error";
            }
            catch (JsonException)
            {
                return "json error";
            }
        });

        var result = await _host.CallAsync(function, Call.New().Data("count", "\"not a number\""));

        result.Body.ShouldBe("\"json error\"");
    }

    [Fact]
    public async Task An_unreadable_body_is_an_internal_error()
    {
        var function = TestFunction.Define(_ => Task.FromResult<object?>(null));

        using var result = await _host.Engine.ExecuteAsync(
            new() { Function = function, Body = "not json"u8.ToArray(), StepId = "step", Services = _host.Services },
            TestContext.Current.CancellationToken);

        result.StatusCode.ShouldBe(500);
        result.NoRetry.ShouldBe(false);
        JsonDocument.Parse(result.Body.WrittenMemory).RootElement.GetProperty("type").GetString().ShouldBe("internal");
    }

    [Fact]
    public async Task A_function_that_cannot_be_created_is_an_internal_error()
    {
        var function = new FunctionDefinition(
            new FunctionOptions { Id = "broken", Triggers = [] },
            _ => throw new InvalidOperationException("missing dependency"));

        var result = await _host.CallAsync(function, Call.New());

        result.StatusCode.ShouldBe(500);
        result.Json.GetProperty("type").GetString().ShouldBe("internal");
        result.Json.GetProperty("message").GetString().ShouldBe("missing dependency");
    }

    [Fact]
    public async Task An_async_void_exception_fails_the_run()
    {
        var function = TestFunction.Define(async context =>
        {
            FireAndForget();
            await context.Step.SleepAsync("wait", TimeSpan.FromHours(1));
            return null;
        });

        var result = await _host.CallAsync(function, Call.New());

        result.StatusCode.ShouldBe(500);
        result.Json.GetProperty("message").GetString().ShouldBe("from async void");
    }

    private static FunctionDefinition SequentialAToB(List<string> ran) => TestFunction.Define(async context =>
    {
        var a = await context.Step.RunAsync("A", () =>
        {
            ran.Add("A");
            return "A";
        });
        var b = await context.Step.RunAsync("B", () =>
        {
            ran.Add("B");
            return "B";
        });
        return a + b;
    });

    private static async void FireAndForget()
    {
        await Task.Yield();
        throw new InvalidOperationException("from async void");
    }

    internal sealed record Order(string Id, decimal Total, IReadOnlyList<string> Items);

    internal sealed record Shaped(string Name, string Message);

    internal sealed record Payload(int Value);

    internal sealed class Unserializable
    {
        public IntPtr Pointer { get; } = IntPtr.Zero;

        public Action Callback { get; } = () => { };
    }
}
