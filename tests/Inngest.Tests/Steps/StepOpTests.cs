using Inngest.Tests.Execution;

namespace Inngest.Tests.Steps;

// The exact ops each step tool reports, per the wire protocol.
public sealed class StepOpTests : IDisposable
{
    private readonly EngineHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Sleep_sends_the_duration_as_name_and_opts_duration()
    {
        var result = await Report(step => step.SleepAsync("sleep-a", TimeSpan.FromMinutes(90)));

        result.Body.ShouldBe(
            """[{"id":"1dca66dd7fafe2a72687a63b9f1c691fdb8b7ba0","op":"Sleep","name":"1h30m","displayName":"sleep-a","opts":{"duration":"1h30m"},"userland":{"id":"sleep-a"}}]""");
    }

    [Fact]
    public async Task Sleep_until_sends_the_time_in_utc()
    {
        var until = new DateTimeOffset(2026, 10, 30, 10, 0, 0, TimeSpan.FromHours(2));

        var result = await Report(step => step.SleepUntilAsync("sleep-a", until));

        result.Body.ShouldBe(
            """[{"id":"1dca66dd7fafe2a72687a63b9f1c691fdb8b7ba0","op":"Sleep","name":"2026-10-30T08:00:00.000Z","displayName":"sleep-a","opts":{"duration":"2026-10-30T08:00:00.000Z"},"userland":{"id":"sleep-a"}}]""");
    }

    [Fact]
    public async Task Wait_for_event_turns_match_into_an_if_expression()
    {
        var result = await Report(step => step.WaitForEventAsync("wait-for-payment", new WaitForEventOptions("app/payment.succeeded", TimeSpan.FromDays(1))
        {
            Match = "data.orderId",
        }));

        result.Body.ShouldBe(
            """[{"id":"5cb60a99e2e4e3944be0f5d63c9a6972d88db4e8","op":"WaitForEvent","name":"app/payment.succeeded","displayName":"wait-for-payment","opts":{"event":"app/payment.succeeded","timeout":"1d","if":"event.data.orderId == async.data.orderId"},"userland":{"id":"wait-for-payment"}}]""");
    }

    [Fact]
    public async Task Wait_for_event_applies_match_and_if_together()
    {
        var result = await Report(step => step.WaitForEventAsync("wait-for-payment", new WaitForEventOptions("app/paid", TimeSpan.FromDays(1))
        {
            Match = "data.orderId",
            If = "async.data.total > 100",
        }));

        System.Text.Json.JsonDocument.Parse(result.Body).RootElement[0].GetProperty("opts").GetProperty("if").GetString()
            .ShouldBe("event.data.orderId == async.data.orderId && (async.data.total > 100)");
    }

    [Fact]
    public async Task Wait_for_event_passes_an_if_expression_through()
    {
        var result = await Report(step => step.WaitForEventAsync("wait-for-payment", new WaitForEventOptions("app/paid", TimeSpan.FromMinutes(30))
        {
            If = "async.data.total > 100",
        }));

        var opts = result.Op().GetProperty("opts");
        opts.GetProperty("timeout").GetString().ShouldBe("30m");
        opts.GetProperty("if").GetString().ShouldBe("async.data.total > 100");
    }

    [Fact]
    public async Task Wait_for_event_without_a_filter_sends_no_if()
    {
        var result = await Report(step => step.WaitForEventAsync("wait-for-payment", new WaitForEventOptions("app/paid", TimeSpan.FromSeconds(0.2))));

        result.Op().GetProperty("opts").GetRawText().ShouldBe("""{"event":"app/paid","timeout":"1s"}""");
    }

    [Fact]
    public async Task Invoke_targets_the_function_in_the_current_app()
    {
        var result = await Report(step => step.InvokeAsync<int>("invoke-child", new InvokeOptions("child-fn")
        {
            Data = new { x = 1 },
            Timeout = TimeSpan.FromHours(1),
        }));

        result.Body.ShouldBe(
            """[{"id":"7ff123e6431be676220df2842b3ee0fd2b288a86","op":"InvokeFunction","displayName":"invoke-child","opts":{"function_id":"test-app-child-fn","payload":{"data":{"x":1}},"timeout":"1h"},"userland":{"id":"invoke-child"}}]""");
    }

    [Fact]
    public async Task Invoke_can_target_another_app_and_send_user_data()
    {
        var result = await Report(step => step.InvokeAsync<int>("invoke-child", new InvokeOptions("child-fn")
        {
            AppId = "billing",
            Data = new Dictionary<string, int> { ["amount"] = 5 },
            User = new { id = "u1" },
        }));

        result.Op().GetProperty("opts").GetRawText().ShouldBe(
            """{"function_id":"billing-child-fn","payload":{"data":{"amount":5},"user":{"id":"u1"}}}""");
    }

    [Fact]
    public async Task Invoke_without_data_sends_an_empty_payload()
    {
        var result = await Report(step => step.InvokeAsync<int>("invoke-child", new InvokeOptions("child-fn")));

        result.Op().GetProperty("opts").GetRawText().ShouldBe("""{"function_id":"test-app-child-fn","payload":{}}""");
    }

    [Fact]
    public async Task Send_event_is_planned_as_a_run_step()
    {
        var function = TestFunction.Define(async context =>
        {
            await Task.WhenAll(
                context.Step.SendEventAsync("send", new InngestEvent("app/shipped", new { orderId = 1 })),
                context.Step.SleepAsync("sleep-a", TimeSpan.FromMinutes(1)));
            return null;
        });

        var result = await _host.CallAsync(function, Call.New());

        result.Op().GetRawText().ShouldBe(
            """{"id":"37ed539c0cef13959abd980bebf30f4922f606e3","op":"StepPlanned","name":"sendEvent","displayName":"send","opts":{"type":"step.sendEvent"},"userland":{"id":"send"}}""");
    }

    [Fact]
    public async Task A_step_execution_reports_how_long_it_took()
    {
        var function = TestFunction.Define(async context => await context.Step.RunAsync("A", () =>
        {
            _host.Time.Advance(TimeSpan.FromMilliseconds(1500));
            return "A";
        }));

        var result = await _host.CallAsync(function, Call.New());

        result.Op().GetProperty("timing").GetRawText().ShouldBe($$$"""{"a":{{{EngineHost.NowNanoseconds}}},"b":1500000000}""");
    }

    [Fact]
    public async Task A_failing_step_is_retried_with_its_error()
    {
        var function = Charge(() => throw new InvalidOperationException("card declined"));

        var result = await _host.CallAsync(function, Call.New().Attempt(0, maxAttempts: 3));

        result.StatusCode.ShouldBe(206);
        result.NoRetry.ShouldBe(false);
        result.RetryAfter.ShouldBeNull();
        var op = result.Op();
        op.GetProperty("id").GetString().ShouldBe("5ea9c348e59fe73a4b15cb287e33e4546dcef782");
        op.GetProperty("op").GetString().ShouldBe("StepError");
        op.GetProperty("name").GetString().ShouldBe("charge");
        op.GetProperty("displayName").GetString().ShouldBe("charge");
        op.GetProperty("opts").GetRawText().ShouldBe("""{"type":"step.run"}""");
        op.GetProperty("userland").GetRawText().ShouldBe("""{"id":"charge"}""");
        op.GetProperty("error").GetProperty("name").GetString().ShouldBe("InvalidOperationException");
        op.GetProperty("error").GetProperty("message").GetString().ShouldBe("card declined");
        op.GetProperty("error").GetProperty("stack").GetString()!.ShouldContain(nameof(StepOpTests));
        op.TryGetProperty("data", out _).ShouldBeFalse();
        op.GetProperty("timing").GetProperty("a").GetInt64().ShouldBe(EngineHost.NowNanoseconds);
    }

    [Fact]
    public async Task A_step_failing_on_its_last_attempt_fails_for_good()
    {
        var function = Charge(() => throw new InvalidOperationException("card declined"));

        var result = await _host.CallAsync(function, Call.New().Attempt(2, maxAttempts: 3));

        result.OpCodes.ShouldBe(["StepFailed"]);
        result.NoRetry.ShouldBe(true);
    }

    [Fact]
    public async Task A_step_throwing_a_non_retriable_exception_fails_for_good_on_any_attempt()
    {
        var function = Charge(() => throw new NonRetriableException("bad card"));

        var result = await _host.CallAsync(function, Call.New().Attempt(0, maxAttempts: 3));

        result.OpCodes.ShouldBe(["StepFailed"]);
        result.NoRetry.ShouldBe(true);
        result.Op().GetProperty("error").GetProperty("name").GetString().ShouldBe("NonRetriableException");
    }

    [Fact]
    public async Task A_step_can_ask_to_be_retried_later()
    {
        var function = Charge(() => throw new RetryAfterException("rate limited", TimeSpan.FromSeconds(10)));

        var result = await _host.CallAsync(function, Call.New());

        result.OpCodes.ShouldBe(["StepError"]);
        result.NoRetry.ShouldBe(false);
        result.RetryAfter.ShouldBe("10");
    }

    [Fact]
    public async Task A_step_whose_output_cannot_be_serialized_fails()
    {
        var function = TestFunction.Define(async context => await context.Step.RunAsync<object>("A", () => new Action(() => { })));

        var result = await _host.CallAsync(function, Call.New());

        result.OpCodes.ShouldBe(["StepError"]);
        result.Op().GetProperty("error").GetProperty("name").GetString().ShouldBe("NotSupportedException");
    }

    [Fact]
    public async Task A_step_handler_gets_the_invocation_cancellation_token()
    {
        using var cancellation = new CancellationTokenSource();
        CancellationToken seen = default;
        var function = TestFunction.Define(async context => await context.Step.RunAsync("A", token =>
        {
            seen = token;
            return Task.FromResult("A");
        }));

        using var result = await _host.Engine.ExecuteAsync(
            new() { Function = function, Body = Call.New().ToBytes(), StepId = "step", Services = _host.Services },
            cancellation.Token);

        seen.ShouldBe(cancellation.Token);
    }

    [Fact]
    public async Task A_step_handler_runs_off_the_scheduler()
    {
        SynchronizationContext? seen = new();
        var function = TestFunction.Define(async context => await context.Step.RunAsync("A", () =>
        {
            seen = SynchronizationContext.Current;
            return "A";
        }));

        await _host.CallAsync(function, Call.New());

        seen.ShouldBeNull();
    }

    [Fact]
    public async Task Invoke_returns_the_memoized_output_of_the_invoked_function()
    {
        var function = TestFunction.Define(async context =>
            (object?)await context.Step.InvokeAsync<Receipt>("invoke-child", new InvokeOptions("child-fn")));

        var result = await _host.CallAsync(function, Call.New().Data("invoke-child", """{"number":"R-1","amount":12}"""));

        result.Body.ShouldBe("""{"number":"R-1","amount":12}""");
    }

    [Fact]
    public async Task Invoke_throws_when_the_invoked_function_failed_or_timed_out()
    {
        var function = TestFunction.Define(async context =>
        {
            try
            {
                await context.Step.InvokeAsync<Receipt>("invoke-child", new InvokeOptions("child-fn"));
                return "no error";
            }
            catch (StepFailedException failure)
            {
                return $"{failure.StepId}|{failure.Error.Name}|{failure.Error.Message}";
            }
        });

        var result = await _host.CallAsync(function, Call.New().Memo(
            "invoke-child",
            """{"error":{"name":"InngestInvokeTimeoutError","message":"Timed out waiting for invoked function to complete","error":"InngestInvokeTimeoutError: Timed out"}}"""));

        result.Body.ShouldBe("\"invoke-child|InngestInvokeTimeoutError|Timed out waiting for invoked function to complete\"");
    }

    [Fact]
    public async Task Wait_for_event_returns_the_matched_event()
    {
        InngestEvent<Paid>? received = null;
        var function = TestFunction.Define(async context =>
        {
            received = await context.Step.WaitForEventAsync<Paid>("wait-for-payment", new WaitForEventOptions("app/paid", TimeSpan.FromDays(1)));
            return null;
        });

        await _host.CallAsync(function, Call.New().Memo(
            "wait-for-payment",
            """{"data":{"orderId":"o-9"},"id":"01JPAID","name":"app/paid","ts":1793268000000,"user":{"plan":"pro"},"v":"2"}"""));

        received.ShouldNotBeNull();
        received.Name.ShouldBe("app/paid");
        received.Data.OrderId.ShouldBe("o-9");
        received.Id.ShouldBe("01JPAID");
        received.Version.ShouldBe("2");
        received.Timestamp.ShouldBe(EngineHost.Now);
        received.User!.Value.GetProperty("plan").GetString().ShouldBe("pro");
    }

    [Fact]
    public async Task Wait_for_event_returns_null_when_it_timed_out()
    {
        var function = TestFunction.Define(async context =>
        {
            var received = await context.Step.WaitForEventAsync("wait-for-payment", new WaitForEventOptions("app/paid", TimeSpan.FromDays(1)));
            return received is null ? "timed out" : "received";
        });

        var result = await _host.CallAsync(function, Call.New().Memo("wait-for-payment", "null"));

        result.Body.ShouldBe("\"timed out\"");
    }

    [Fact]
    public async Task A_finished_sleep_lets_the_function_continue()
    {
        var function = TestFunction.Define(async context =>
        {
            await context.Step.SleepAsync("sleep-a", TimeSpan.FromDays(3));
            await context.Step.SleepUntilAsync("zzz", EngineHost.Now.AddDays(7));
            return "awake";
        });

        var result = await _host.CallAsync(function, Call.New().Memo("sleep-a", "null").Memo("zzz", "null"));

        result.Body.ShouldBe("\"awake\"");
    }

    [Fact]
    public async Task Send_event_returns_the_memoized_event_ids()
    {
        var function = TestFunction.Define(async context =>
        {
            var sent = await context.Step.SendEventAsync("send", new InngestEvent("app/shipped"));
            return string.Join(",", sent.Ids);
        });

        var result = await _host.CallAsync(function, Call.New().Data("send", """{"ids":["01JA","01JB"]}"""));

        result.Body.ShouldBe("\"01JA,01JB\"");
    }

    [Fact]
    public async Task Invalid_step_arguments_throw_at_the_call_site()
    {
        var errors = new List<string>();
        var function = TestFunction.Define(context =>
        {
            void Expect(Action call)
            {
                try
                {
                    call();
                }
                catch (ArgumentException exception)
                {
                    errors.Add(exception.ParamName ?? "?");
                }
            }

            Expect(() => context.Step.RunAsync("", () => 1));
            Expect(() => context.Step.RunAsync("a", (Func<CancellationToken, Task<int>>)null!));
            Expect(() => context.Step.SleepAsync("b", TimeSpan.FromSeconds(-1)));
            Expect(() => context.Step.WaitForEventAsync("c", new WaitForEventOptions("", TimeSpan.FromMinutes(1))));
            Expect(() => context.Step.InvokeAsync<int>("d", new InvokeOptions("")));
            Expect(() => context.Step.SendEventAsync("e", (IReadOnlyList<InngestEvent>)null!));
            return Task.FromResult<object?>(null);
        });

        await _host.CallAsync(function, Call.New());

        errors.ShouldBe(["id", "handler", "duration", "options.Event", "options.FunctionId", "events"]);
    }

    private Task<CallResult> Report(Func<IStepTools, Task> step) =>
        _host.CallAsync(
            TestFunction.Define(async context =>
            {
                await step(context.Step);
                return null;
            }),
            Call.New());

    private static Inngest.Functions.FunctionDefinition Charge(Func<string> charge) =>
        TestFunction.Define(async context => await context.Step.RunAsync("charge", charge));

    internal sealed record Receipt(string Number, int Amount);

    internal sealed record Paid(string OrderId);
}
