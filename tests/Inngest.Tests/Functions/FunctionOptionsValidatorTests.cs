using Inngest.Functions;

namespace Inngest.Tests.Functions;

public sealed class FunctionOptionsValidatorTests
{
    private static readonly FunctionOptions Valid = new()
    {
        Id = "orders",
        Triggers = [Trigger.Event("order/created")],
    };

    [Fact]
    public void Accepts_a_function_with_only_an_id_and_a_trigger() => ShouldAccept(Valid);

    [Fact]
    public void Rejects_missing_options() =>
        Should.Throw<ArgumentNullException>(() => FunctionOptionsValidator.Validate(null!));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_an_empty_id(string id)
    {
        var exception = Should.Throw<InvalidOperationException>(() => FunctionOptionsValidator.Validate(Valid with { Id = id }));

        exception.Message.ShouldBe("A function has no Id. Set FunctionOptions.Id to an id that is unique in the app and never changes.");
    }

    [Fact]
    public void Names_the_function_by_its_name_when_the_id_is_empty()
    {
        var exception = Should.Throw<InvalidOperationException>(
            () => FunctionOptionsValidator.Validate(Valid with { Id = "", Name = "Process orders" }));

        exception.Message.ShouldBe("The function named \"Process orders\" has no Id. Set FunctionOptions.Id to an id that is unique in the app and never changes.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(20)]
    public void Accepts_retries_from_zero_to_twenty(int? retries) => ShouldAccept(Valid with { Retries = retries });

    [Theory]
    [InlineData(-1)]
    [InlineData(21)]
    [InlineData(1_000)]
    public void Rejects_retries_outside_zero_to_twenty(int retries) =>
        ShouldReject(Valid with { Retries = retries }, $"Retries must be between 0 and 20, but was {retries}.");

    [Fact]
    public void Accepts_a_function_without_triggers_because_it_can_be_invoked() =>
        ShouldAccept(Valid with { Triggers = [] });

    [Fact]
    public void Accepts_ten_triggers() => ShouldAccept(Valid with { Triggers = Events(10) });

    [Fact]
    public void Rejects_eleven_triggers() =>
        ShouldReject(Valid with { Triggers = Events(11) }, "A function can have at most 10 triggers, but has 11.");

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Rejects_an_event_trigger_without_an_event_name(string name) =>
        ShouldReject(Valid with { Triggers = [new EventTrigger(name)] }, "An event trigger needs an event name.");

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Rejects_a_cron_trigger_without_a_schedule(string schedule) =>
        ShouldReject(Valid with { Triggers = [new CronTrigger(schedule)] }, "A cron trigger needs a schedule.");

    [Fact]
    public void Accepts_one_or_two_concurrency_limits()
    {
        ShouldAccept(Valid with { Concurrency = [new ConcurrencyLimit(5)] });
        ShouldAccept(Valid with { Concurrency = [new ConcurrencyLimit(5), new ConcurrencyLimit(1) { Key = "event.data.accountId", Scope = ConcurrencyScope.Account }] });
        ShouldAccept(Valid with { Concurrency = [] });
    }

    [Fact]
    public void Rejects_three_concurrency_limits()
    {
        var limits = new[] { new ConcurrencyLimit(5), new ConcurrencyLimit(4), new ConcurrencyLimit(3) };

        ShouldReject(Valid with { Concurrency = limits }, "Concurrency allows at most 2 limits, but 3 were given.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Rejects_a_concurrency_limit_that_is_not_positive(int limit) =>
        ShouldReject(
            Valid with { Concurrency = [new ConcurrencyLimit(limit)] },
            $"A concurrency limit must be greater than 0, but one was {limit}.");

    [Fact]
    public void Checks_every_concurrency_limit()
    {
        var limits = new[] { new ConcurrencyLimit(5), new ConcurrencyLimit(0) };

        ShouldReject(Valid with { Concurrency = limits }, "A concurrency limit must be greater than 0, but one was 0.");
    }

    [Theory]
    [InlineData(ConcurrencyScope.Environment, null)]
    [InlineData(ConcurrencyScope.Environment, "")]
    [InlineData(ConcurrencyScope.Environment, "  ")]
    [InlineData(ConcurrencyScope.Account, null)]
    public void Rejects_a_concurrency_scope_other_than_the_function_without_a_key(ConcurrencyScope scope, string? key) =>
        ShouldReject(
            Valid with { Concurrency = [new ConcurrencyLimit(5) { Scope = scope, Key = key }] },
            $"A concurrency limit with the {scope} scope needs a Key. Only the Function scope can go without one.");

    [Theory]
    [InlineData(ConcurrencyScope.Function, null)]
    [InlineData(ConcurrencyScope.Function, "event.data.accountId")]
    [InlineData(ConcurrencyScope.Environment, "event.data.accountId")]
    [InlineData(ConcurrencyScope.Account, "event.data.accountId")]
    public void Accepts_scopes_that_have_what_they_need(ConcurrencyScope scope, string? key) =>
        ShouldAccept(Valid with { Concurrency = [new ConcurrencyLimit(5) { Scope = scope, Key = key }] });

    [Fact]
    public void Rejects_a_concurrency_scope_that_does_not_exist() =>
        ShouldReject(
            Valid with { Concurrency = [new ConcurrencyLimit(5) { Scope = (ConcurrencyScope)7 }] },
            "7 is not a valid ConcurrencyScope.");

    [Fact]
    public void Accepts_a_throttle_at_its_limits() =>
        ShouldAccept(Valid with { Throttle = new Throttle(1, TimeSpan.FromSeconds(1)) { Burst = 0 } });

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Rejects_a_throttle_limit_that_is_not_positive(int limit) =>
        ShouldReject(Valid with { Throttle = new Throttle(limit, TimeSpan.FromMinutes(1)) }, $"Throttle.Limit must be greater than 0, but was {limit}.");

    [Theory]
    [InlineData(500, "0.5s")]
    [InlineData(0, "0s")]
    [InlineData(-1_000, "-1s")]
    public void Rejects_a_throttle_period_under_a_second(int milliseconds, string described) =>
        ShouldReject(
            Valid with { Throttle = new Throttle(1, TimeSpan.FromMilliseconds(milliseconds)) },
            $"Throttle.Period must be at least 1s, but was {described}.");

    [Fact]
    public void Rejects_a_negative_throttle_burst() =>
        ShouldReject(
            Valid with { Throttle = new Throttle(1, TimeSpan.FromMinutes(1)) { Burst = -1 } },
            "Throttle.Burst must not be negative, but was -1.");

    [Fact]
    public void Accepts_a_rate_limit_from_one_second_to_a_day()
    {
        ShouldAccept(Valid with { RateLimit = new RateLimit(1, TimeSpan.FromSeconds(1)) });
        ShouldAccept(Valid with { RateLimit = new RateLimit(1, TimeSpan.FromHours(24)) });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Rejects_a_rate_limit_that_is_not_positive(int limit) =>
        ShouldReject(Valid with { RateLimit = new RateLimit(limit, TimeSpan.FromHours(1)) }, $"RateLimit.Limit must be greater than 0, but was {limit}.");

    [Theory]
    [InlineData(999, "0.999s")]
    [InlineData(86_401_000, "1d1s")]
    [InlineData(604_800_000, "1w")]
    public void Rejects_a_rate_limit_period_outside_a_second_to_a_day(int milliseconds, string described) =>
        ShouldReject(
            Valid with { RateLimit = new RateLimit(1, TimeSpan.FromMilliseconds(milliseconds)) },
            $"RateLimit.Period must be between 1s and 24h, but was {described}.");

    [Fact]
    public void Accepts_a_debounce_from_one_second_to_seven_days()
    {
        ShouldAccept(Valid with { Debounce = new Debounce(TimeSpan.FromSeconds(1)) });
        ShouldAccept(Valid with { Debounce = new Debounce(TimeSpan.FromDays(7)) { Timeout = TimeSpan.FromDays(30) } });
    }

    [Theory]
    [InlineData(999, "0.999s")]
    [InlineData(0, "0s")]
    [InlineData(604_801_000, "1w1s")]
    public void Rejects_a_debounce_period_outside_a_second_to_seven_days(int milliseconds, string described) =>
        ShouldReject(
            Valid with { Debounce = new Debounce(TimeSpan.FromMilliseconds(milliseconds)) },
            $"Debounce.Period must be between 1s and 7d, but was {described}.");

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(-60_000, "-60s")]
    public void Rejects_a_debounce_timeout_that_is_not_positive(int milliseconds, string described) =>
        ShouldReject(
            Valid with { Debounce = new Debounce(TimeSpan.FromSeconds(5)) { Timeout = TimeSpan.FromMilliseconds(milliseconds) } },
            $"Debounce.Timeout must be greater than zero, but was {described}.");

    [Fact]
    public void Accepts_batching_at_its_limits()
    {
        ShouldAccept(Valid with { BatchEvents = new BatchEvents(2, TimeSpan.FromSeconds(1)) });
        ShouldAccept(Valid with { BatchEvents = new BatchEvents(100, TimeSpan.FromSeconds(60)) { Key = "event.data.tenant", If = "event.data.batch" } });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-10)]
    public void Rejects_a_batch_of_fewer_than_two_events(int maxSize) =>
        ShouldReject(
            Valid with { BatchEvents = new BatchEvents(maxSize, TimeSpan.FromSeconds(5)) },
            $"BatchEvents.MaxSize must be at least 2, but was {maxSize}.");

    [Fact]
    public void Rejects_a_batch_timeout_under_one_second() =>
        ShouldReject(
            Valid with { BatchEvents = new BatchEvents(10, TimeSpan.FromMilliseconds(999)) },
            "BatchEvents.Timeout must be at least 1s, but was 0.999s.");

    [Fact]
    public void Leaves_the_upper_batch_timeout_to_the_plan() =>
        Should.NotThrow(() => FunctionOptionsValidator.Validate(Valid with { BatchEvents = new BatchEvents(10, TimeSpan.FromMinutes(5)) }));

    [Fact]
    public void Rejects_batching_together_with_cancellation()
    {
        var options = Valid with
        {
            BatchEvents = new BatchEvents(10, TimeSpan.FromSeconds(5)),
            CancelOn = [new CancelOn("order/cancelled")],
        };

        ShouldReject(options, "BatchEvents cannot be combined with CancelOn.");
    }

    [Fact]
    public void Rejects_batching_together_with_debounce()
    {
        var options = Valid with
        {
            BatchEvents = new BatchEvents(10, TimeSpan.FromSeconds(5)),
            Debounce = new Debounce(TimeSpan.FromSeconds(5)),
        };

        ShouldReject(options, "BatchEvents cannot be combined with Debounce.");
    }

    [Fact]
    public void Rejects_batching_together_with_a_singleton()
    {
        var options = Valid with
        {
            BatchEvents = new BatchEvents(10, TimeSpan.FromSeconds(5)),
            Singleton = new Singleton(SingletonMode.Skip),
        };

        ShouldReject(options, "BatchEvents cannot be combined with Singleton.");
    }

    [Fact]
    public void Rejects_batching_together_with_a_priority()
    {
        var options = Valid with
        {
            BatchEvents = new BatchEvents(10, TimeSpan.FromSeconds(5)),
            Priority = new Priority("event.data.vip ? 60 : 0"),
        };

        ShouldReject(options, "BatchEvents cannot be combined with Priority.");
    }

    [Fact]
    public void An_empty_cancellation_list_does_not_conflict_with_batching() =>
        ShouldAccept(Valid with { BatchEvents = new BatchEvents(10, TimeSpan.FromSeconds(5)), CancelOn = [] });

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Rejects_a_priority_without_an_expression(string run) =>
        ShouldReject(Valid with { Priority = new Priority(run) }, "Priority.Run needs an expression, for example \"event.data.vip ? 120 : 0\".");

    [Fact]
    public void Accepts_five_cancellations() =>
        ShouldAccept(Valid with { CancelOn = [.. Enumerable.Range(0, 5).Select(i => new CancelOn($"order/cancelled.{i}"))] });

    [Fact]
    public void Rejects_six_cancellations() =>
        ShouldReject(
            Valid with { CancelOn = [.. Enumerable.Range(0, 6).Select(i => new CancelOn($"order/cancelled.{i}"))] },
            "CancelOn allows at most 5 entries, but 6 were given.");

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Rejects_a_cancellation_without_an_event_name(string name) =>
        ShouldReject(Valid with { CancelOn = [new CancelOn(name)] }, "A CancelOn entry needs an event name.");

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(-1_000, "-1s")]
    public void Rejects_a_cancellation_timeout_that_is_not_positive(int milliseconds, string described) =>
        ShouldReject(
            Valid with { CancelOn = [new CancelOn("order/cancelled") { Timeout = TimeSpan.FromMilliseconds(milliseconds) }] },
            $"The CancelOn timeout for \"order/cancelled\" must be greater than zero, but was {described}.");

    [Fact]
    public void Accepts_cancellations_with_a_timeout_a_condition_and_a_match() =>
        ShouldAccept(Valid with
        {
            CancelOn =
            [
                new CancelOn("order/cancelled") { Timeout = TimeSpan.FromDays(365) },
                new CancelOn("order/refunded") { If = "async.data.id == event.data.id" },
                new CancelOn("order/paused") { Match = "data.id" },
            ],
        });

    [Fact]
    public void Accepts_function_timeouts()
    {
        ShouldAccept(Valid with { Timeouts = new FunctionTimeouts() });
        ShouldAccept(Valid with { Timeouts = new FunctionTimeouts { Start = TimeSpan.FromMinutes(1) } });
        ShouldAccept(Valid with { Timeouts = new FunctionTimeouts { Start = TimeSpan.FromMinutes(1), Finish = TimeSpan.FromHours(1) } });
    }

    [Fact]
    public void Rejects_a_start_timeout_that_is_not_positive() =>
        ShouldReject(
            Valid with { Timeouts = new FunctionTimeouts { Start = TimeSpan.Zero } },
            "Timeouts.Start must be greater than zero, but was 0s.");

    [Fact]
    public void Rejects_a_finish_timeout_that_is_not_positive() =>
        ShouldReject(
            Valid with { Timeouts = new FunctionTimeouts { Finish = TimeSpan.FromSeconds(-30) } },
            "Timeouts.Finish must be greater than zero, but was -30s.");

    [Theory]
    [InlineData(SingletonMode.Skip)]
    [InlineData(SingletonMode.Cancel)]
    public void Accepts_both_singleton_modes(SingletonMode mode) =>
        ShouldAccept(Valid with { Singleton = new Singleton(mode) { Key = "event.data.userId" } });

    [Fact]
    public void Rejects_a_singleton_mode_that_does_not_exist() =>
        ShouldReject(Valid with { Singleton = new Singleton((SingletonMode)9) }, "9 is not a valid SingletonMode.");

    [Fact]
    public void Accepts_every_option_at_once_where_inngest_allows_it()
    {
        var options = Valid with
        {
            Retries = 20,
            Triggers = Events(10),
            Concurrency = [new ConcurrencyLimit(2), new ConcurrencyLimit(1) { Key = "event.data.id", Scope = ConcurrencyScope.Environment }],
            Throttle = new Throttle(1, TimeSpan.FromSeconds(1)) { Burst = 3 },
            RateLimit = new RateLimit(1, TimeSpan.FromHours(24)),
            Debounce = new Debounce(TimeSpan.FromSeconds(1)) { Timeout = TimeSpan.FromHours(1) },
            Priority = new Priority("0"),
            Idempotency = "event.data.id",
            CancelOn = [.. Enumerable.Range(0, 5).Select(i => new CancelOn($"order/cancelled.{i}"))],
            Timeouts = new FunctionTimeouts { Start = TimeSpan.FromSeconds(1), Finish = TimeSpan.FromSeconds(1) },
            Singleton = new Singleton(SingletonMode.Cancel),
        };

        ShouldAccept(options);
    }

    private static EventTrigger[] Events(int count) =>
        [.. Enumerable.Range(0, count).Select(i => Trigger.Event($"order/event.{i}"))];

    private static void ShouldAccept(FunctionOptions options) =>
        Should.NotThrow(() => FunctionOptionsValidator.Validate(options));

    private static void ShouldReject(FunctionOptions options, string problem)
    {
        var exception = Should.Throw<InvalidOperationException>(() => FunctionOptionsValidator.Validate(options));

        exception.Message.ShouldBe($"Function \"orders\": {problem}");
    }
}
