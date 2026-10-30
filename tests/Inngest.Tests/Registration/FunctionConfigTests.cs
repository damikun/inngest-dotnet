using static Inngest.Tests.Registration.RegistryFixtures;

namespace Inngest.Tests.Registration;

public sealed class FunctionConfigTests
{
    // Each row is an option and the exact JSON that a sync carries for it.
    public static TheoryData<string, FunctionOptions, string> Options => new()
    {
        {
            "triggers",
            Orders with { Triggers = [] },
            "[]"
        },
        {
            "triggers",
            Orders with { Triggers = [Trigger.Event("order/created", "event.data.total > 100")] },
            """[{"event":"order/created","expression":"event.data.total > 100"}]"""
        },
        {
            "triggers",
            Orders with { Triggers = [Trigger.Cron("0 9 * * 1-5")] },
            """[{"cron":"0 9 * * 1-5"}]"""
        },
        {
            "triggers",
            Orders with { Triggers = [Trigger.Event("a"), Trigger.Cron("* * * * *"), Trigger.Event("b", "event.data.x == 1")] },
            """[{"event":"a"},{"cron":"* * * * *"},{"event":"b","expression":"event.data.x == 1"}]"""
        },
        {
            "concurrency",
            Orders with { Concurrency = [new ConcurrencyLimit(10)] },
            """[{"limit":10,"scope":"fn"}]"""
        },
        {
            "concurrency",
            Orders with { Concurrency = [new ConcurrencyLimit(0), new ConcurrencyLimit(10) { Key = "event.data.accountId" }] },
            """[{"limit":10,"key":"event.data.accountId","scope":"fn"}]"""
        },
        {
            "concurrency",
            Orders with { Concurrency = [new ConcurrencyLimit(10) { Key = "event.data.accountId" }] },
            """[{"limit":10,"key":"event.data.accountId","scope":"fn"}]"""
        },
        {
            "concurrency",
            Orders with { Concurrency = [new ConcurrencyLimit(1) { Key = "event.data.accountId", Scope = ConcurrencyScope.Environment }] },
            """[{"limit":1,"key":"event.data.accountId","scope":"env"}]"""
        },
        {
            "concurrency",
            Orders with { Concurrency = [new ConcurrencyLimit(5) { Key = "event.data.accountId", Scope = ConcurrencyScope.Account }] },
            """[{"limit":5,"key":"event.data.accountId","scope":"account"}]"""
        },
        {
            "concurrency",
            Orders with
            {
                Concurrency =
                [
                    new ConcurrencyLimit(10),
                    new ConcurrencyLimit(1) { Key = "event.data.accountId", Scope = ConcurrencyScope.Account },
                ],
            },
            """[{"limit":10,"scope":"fn"},{"limit":1,"key":"event.data.accountId","scope":"account"}]"""
        },
        {
            "throttle",
            Orders with { Throttle = new Throttle(5, TimeSpan.FromMinutes(1)) },
            """{"limit":5,"period":"1m"}"""
        },
        {
            "throttle",
            Orders with { Throttle = new Throttle(5, TimeSpan.FromMinutes(1)) { Burst = 2, Key = "event.data.accountId" } },
            """{"limit":5,"period":"1m","burst":2,"key":"event.data.accountId"}"""
        },
        {
            "throttle",
            Orders with { Throttle = new Throttle(1, TimeSpan.FromMinutes(90)) { Burst = 0 } },
            """{"limit":1,"period":"1h30m","burst":0}"""
        },
        {
            "rateLimit",
            Orders with { RateLimit = new RateLimit(1, TimeSpan.FromHours(4)) },
            """{"limit":1,"period":"4h"}"""
        },
        {
            "rateLimit",
            Orders with { RateLimit = new RateLimit(3, TimeSpan.FromHours(24)) { Key = "event.data.accountId" } },
            """{"limit":3,"period":"1d","key":"event.data.accountId"}"""
        },
        {
            "idempotency",
            Orders with { Idempotency = "event.data.orderId" },
            "\"event.data.orderId\""
        },
        {
            "debounce",
            Orders with { Debounce = new Debounce(TimeSpan.FromSeconds(5)) },
            """{"period":"5s"}"""
        },
        {
            "debounce",
            Orders with { Debounce = new Debounce(TimeSpan.FromSeconds(5)) { Key = "event.data.accountId", Timeout = TimeSpan.FromMinutes(1) } },
            """{"period":"5s","key":"event.data.accountId","timeout":"1m"}"""
        },
        {
            "debounce",
            Orders with { Debounce = new Debounce(TimeSpan.FromDays(7)) },
            """{"period":"1w"}"""
        },
        {
            "batchEvents",
            Orders with { BatchEvents = new BatchEvents(50, TimeSpan.FromSeconds(5)) },
            """{"maxSize":50,"timeout":"5s"}"""
        },
        {
            "batchEvents",
            Orders with { BatchEvents = new BatchEvents(50, TimeSpan.FromSeconds(5)) { Key = "event.data.tenant", If = "event.data.batch == true" } },
            """{"maxSize":50,"timeout":"5s","key":"event.data.tenant","if":"event.data.batch == true"}"""
        },
        {
            "batchEvents",
            Orders with { BatchEvents = new BatchEvents(2, TimeSpan.FromSeconds(1)) },
            """{"maxSize":2,"timeout":"1s"}"""
        },
        {
            "batchEvents",
            Orders with { BatchEvents = new BatchEvents(100, TimeSpan.FromSeconds(45)) },
            """{"maxSize":100,"timeout":"45s"}"""
        },
        {
            "batchEvents",
            Orders with { BatchEvents = new BatchEvents(100, TimeSpan.FromSeconds(60)) },
            """{"maxSize":100,"timeout":"1m"}"""
        },
        {
            "priority",
            Orders with { Priority = new Priority("event.data.vip ? 120 : 0") },
            """{"run":"event.data.vip ? 120 : 0"}"""
        },
        {
            "cancel",
            Orders with { CancelOn = [new CancelOn("user/deleted")] },
            """[{"event":"user/deleted"}]"""
        },
        {
            "cancel",
            Orders with { CancelOn = [new CancelOn("user/deleted") { Timeout = TimeSpan.FromDays(1) }] },
            """[{"event":"user/deleted","timeout":"1d"}]"""
        },
        {
            "cancel",
            Orders with { CancelOn = [new CancelOn("user/deleted") { If = "event.data.userId == async.data.userId" }] },
            """[{"event":"user/deleted","if":"event.data.userId == async.data.userId"}]"""
        },
        {
            "cancel",
            Orders with { CancelOn = [new CancelOn("user/deleted") { Match = "data.userId", Timeout = TimeSpan.FromHours(2) }] },
            """[{"event":"user/deleted","timeout":"2h","if":"event.data.userId == async.data.userId"}]"""
        },
        {
            "cancel",
            Orders with { CancelOn = [new CancelOn("user/deleted") { Timeout = TimeSpan.FromMilliseconds(400) }] },
            """[{"event":"user/deleted","timeout":"1s"}]"""
        },
        {
            "timeouts",
            Orders with { Timeouts = new FunctionTimeouts { Start = TimeSpan.FromMinutes(10) } },
            """{"start":"10m"}"""
        },
        {
            "timeouts",
            Orders with { Timeouts = new FunctionTimeouts { Finish = TimeSpan.FromHours(1) } },
            """{"finish":"1h"}"""
        },
        {
            "timeouts",
            Orders with { Timeouts = new FunctionTimeouts { Start = TimeSpan.FromMinutes(10), Finish = TimeSpan.FromHours(1) } },
            """{"start":"10m","finish":"1h"}"""
        },
        {
            "singleton",
            Orders with { Singleton = new Singleton(SingletonMode.Skip) },
            """{"mode":"skip"}"""
        },
        {
            "singleton",
            Orders with { Singleton = new Singleton(SingletonMode.Cancel) { Key = "event.data.userId" } },
            """{"mode":"cancel","key":"event.data.userId"}"""
        },
    };

    [Theory]
    [MemberData(nameof(Options))]
    public void Writes_each_option_as_inngest_expects_it(string property, FunctionOptions options, string expected) =>
        Property(options, property).ShouldBe(expected);

    [Fact]
    public void Writes_nothing_for_options_that_are_not_set()
    {
        var function = Parse(Function(Orders));

        function.EnumerateObject().Select(property => property.Name).ShouldBe(["id", "name", "triggers", "steps"]);
    }

    public static TheoryData<string, FunctionOptions> UnsetOptions => new()
    {
        { "concurrency", Orders with { Concurrency = [] } },

        // 0 means no limit.
        { "concurrency", Orders with { Concurrency = [new ConcurrencyLimit(0)] } },
        { "idempotency", Orders with { Idempotency = "" } },
        { "idempotency", Orders with { Idempotency = " " } },
        { "cancel", Orders with { CancelOn = [] } },
        { "timeouts", Orders with { Timeouts = new FunctionTimeouts() } },
    };

    [Theory]
    [MemberData(nameof(UnsetOptions))]
    public void Leaves_out_options_that_are_present_but_empty(string property, FunctionOptions options) =>
        Property(options, property).ShouldBeNull();

    [Theory]
    [InlineData("0 9 * * 1-5", null, "0 9 * * 1-5")]
    [InlineData("0 9 * * 1-5", "", "0 9 * * 1-5")]
    [InlineData("0 9 * * 1-5", " ", "0 9 * * 1-5")]
    [InlineData("0 9 * * 1-5", "Europe/Prague", "TZ=Europe/Prague 0 9 * * 1-5")]
    [InlineData("* * * * *", "America/Argentina/Buenos_Aires", "TZ=America/Argentina/Buenos_Aires * * * * *")]
    [InlineData("TZ=Europe/Prague 0 9 * * 1-5", null, "TZ=Europe/Prague 0 9 * * 1-5")]
    [InlineData("TZ=Europe/Prague 0 9 * * 1-5", "UTC", "TZ=Europe/Prague 0 9 * * 1-5")]
    [InlineData("CRON_TZ=Europe/Prague 0 9 * * 1-5", "UTC", "CRON_TZ=Europe/Prague 0 9 * * 1-5")]
    public void Cron_time_zone_becomes_a_prefix_unless_the_schedule_has_one(string schedule, string? timezone, string expected) =>
        Property(Orders with { Triggers = [Trigger.Cron(schedule, timezone)] }, "triggers").ShouldBe($$"""[{"cron":"{{expected}}"}]""");

    [Theory]
    [InlineData(null, "orders")]
    [InlineData("", "orders")]
    [InlineData("  ", "orders")]
    [InlineData("Process orders", "Process orders")]
    public void Name_defaults_to_the_function_id(string? name, string expected) =>
        Parse(Function(Orders with { Name = name })).GetProperty("name").GetString().ShouldBe(expected);

    [Fact]
    public void Writes_retries_as_the_attempts_field() =>
        Retries(Orders with { Retries = 5 }).ShouldBe("""{"attempts":5}""");

    [Fact]
    public void Zero_retries_are_written_because_they_differ_from_the_default() =>
        Retries(Orders with { Retries = 0 }).ShouldBe("""{"attempts":0}""");

    [Fact]
    public void Leaves_retries_out_so_that_inngest_uses_its_default() =>
        Retries(Orders).ShouldBeNull();

    [Fact]
    public void Several_concurrency_limits_keep_the_order_they_were_given_in()
    {
        var options = Orders with
        {
            Concurrency = [new ConcurrencyLimit(50), new ConcurrencyLimit(5) { Key = "event.data.accountId", Scope = ConcurrencyScope.Environment }],
        };

        Property(options, "concurrency").ShouldBe(
            """[{"limit":50,"scope":"fn"},{"limit":5,"key":"event.data.accountId","scope":"env"}]""");
    }

    [Fact]
    public void Cancel_match_and_condition_both_apply()
    {
        var options = Orders with
        {
            CancelOn = [new CancelOn("user/deleted") { If = "async.data.reason != 'test'", Match = "data.userId" }],
        };

        Property(options, "cancel").ShouldBe(
            """[{"event":"user/deleted","if":"event.data.userId == async.data.userId && (async.data.reason != 'test')"}]""");
    }

    [Fact]
    public void Cancel_condition_is_used_when_match_is_blank()
    {
        var options = Orders with
        {
            CancelOn = [new CancelOn("user/deleted") { If = "async.data.id == event.data.id", Match = " " }],
        };

        Property(options, "cancel").ShouldBe("""[{"event":"user/deleted","if":"async.data.id == event.data.id"}]""");
    }

    [Fact]
    public void Writes_each_cancellation_in_order()
    {
        var options = Orders with
        {
            CancelOn =
            [
                new CancelOn("user/deleted") { Match = "data.userId" },
                new CancelOn("user/paused") { Timeout = TimeSpan.FromMinutes(1) },
            ],
        };

        Property(options, "cancel").ShouldBe(
            """[{"event":"user/deleted","if":"event.data.userId == async.data.userId"},{"event":"user/paused","timeout":"1m"}]""");
    }

    [Theory]
    [InlineData(1, "1s")]
    [InlineData(30, "30s")]
    [InlineData(59, "59s")]
    [InlineData(60, "1m")]
    public void Batch_timeouts_only_use_the_units_that_go_parses(int seconds, string expected) =>
        Parse(Property(Orders with { BatchEvents = new BatchEvents(10, TimeSpan.FromSeconds(seconds)) }, "batchEvents")!)
            .GetProperty("timeout").GetString().ShouldBe(expected);

    [Fact]
    public void Writes_options_in_a_fixed_order()
    {
        var options = Orders with
        {
            Concurrency = [new ConcurrencyLimit(10)],
            Throttle = new Throttle(5, TimeSpan.FromMinutes(1)),
            Debounce = new Debounce(TimeSpan.FromSeconds(5)),
            Singleton = new Singleton(SingletonMode.Skip),
            Priority = new Priority("0"),
            CancelOn = [new CancelOn("order/cancelled")],
        };

        Parse(Function(options)).EnumerateObject().Select(property => property.Name).ShouldBe(
            ["id", "name", "triggers", "steps", "concurrency", "throttle", "debounce", "priority", "cancel", "singleton"]);
    }

    [Fact]
    public void Batching_can_be_combined_with_concurrency()
    {
        var options = Orders with
        {
            BatchEvents = new BatchEvents(10, TimeSpan.FromSeconds(5)),
            Concurrency = [new ConcurrencyLimit(2)],
        };

        Should.NotThrow(() => Registry(options));
    }

    private static string? Retries(FunctionOptions options)
    {
        var step = Parse(Function(options)).GetProperty("steps").GetProperty("step");
        return step.TryGetProperty("retries", out var retries) ? retries.GetRawText() : null;
    }
}
