using Inngest.SourceGeneration.Functions;
using Inngest.SourceGeneration.Tests.Support;
using Microsoft.CodeAnalysis.Testing;

namespace Inngest.SourceGeneration.Tests.Functions;

public sealed class FunctionOptionsAnalyzerTests
{
    private const string Fields = "it has 6 fields, and Inngest expects 5: minute, hour, day of month, month and day of week";
    private const string NotATimeString = "is not a time string; use whole numbers with w, d, h, m or s, such as \"30s\", \"5m\" or \"1h30m\"";

    // INNGEST030

    [Fact]
    public async Task A_cron_schedule_that_Inngest_cannot_read_is_reported()
    {
        const string source = """
            [InngestFunction("report")]
            [CronTrigger({|#0:"0 0 9 * * *"|})]
            [CronTrigger({|#1:"@daily"|})]
            [CronTrigger(schedule: {|#2:"0 24 * * *"|}, Timezone = "Europe/Prague")]
            public sealed class Report : InngestFunction<JsonElement>
            {
                public override Task<object?> ExecuteAsync(FunctionContext<JsonElement> context, CancellationToken cancellationToken) => Task.FromResult<object?>(null);
            }
            """;

        await Verify(
            source,
            Cron(0, "0 0 9 * * *", Fields),
            Cron(1, "@daily", "Inngest does not accept descriptors such as @daily; write the five fields instead"),
            Cron(2, "0 24 * * *", "the hour 24 is above 23"));
    }

    [Fact]
    public async Task A_cron_schedule_in_the_options_of_a_delegate_function_is_reported()
    {
        const string source = """
            public static class Setup
            {
                public static FunctionTrigger[] Triggers() =>
                [
                    Trigger.Cron({|#0:"0 9 * * 1-7"|}),
                    new CronTrigger({|#1:"0 0 9 * * *"|}),
                    Trigger.Cron("0 9 * * 1-5", timezone: "Europe/Prague"),
                    new CronTrigger("TZ=America/New_York 30 17 * * 5") { Timezone = "Europe/Prague" },
                ];
            }
            """;

        await Verify(source, Cron(0, "0 9 * * 1-7", "the day of week 7 is above 6"), Cron(1, "0 0 9 * * *", Fields));
    }

    [Fact]
    public async Task A_schedule_with_the_time_zone_in_front_is_checked_whole()
    {
        var timezone = new string('x', 250);
        var source = $$"""
            [InngestFunction("report")]
            [CronTrigger({|#0:"0 9 * * *"|}, Timezone = "{{timezone}}")]
            public sealed class Report : InngestFunction<JsonElement>
            {
                public override Task<object?> ExecuteAsync(FunctionContext<JsonElement> context, CancellationToken cancellationToken) => Task.FromResult<object?>(null);
            }
            """;

        await Verify(source, Cron(0, "0 9 * * *", "it is longer than 255 characters"));
    }

    [Fact]
    public async Task Valid_schedules_and_ones_not_known_at_build_time_are_fine()
    {
        const string source = """
            [InngestFunction("report")]
            [CronTrigger("0 9 * * MON-FRI", Timezone = "Europe/Prague", Jitter = "30s")]
            [CronTrigger("*/15 0-6,22-23 1,15 jan-jun ?")]
            [CronTrigger("TZ=America/New_York 30 17 * * 5")]
            public sealed class Report : InngestFunction<JsonElement>
            {
                public override Task<object?> ExecuteAsync(FunctionContext<JsonElement> context, CancellationToken cancellationToken) => Task.FromResult<object?>(null);

                // The validator at startup reports an empty schedule with a message of its own.
                public static FunctionTrigger[] Triggers(string schedule) => [Trigger.Cron(schedule), new CronTrigger(""), Trigger.Cron("0 2 * * *")];
            }
            """;

        await Verify(source);
    }

    // INNGEST031

    [Fact]
    public async Task A_time_string_the_attribute_cannot_parse_is_reported()
    {
        const string source = """
            [InngestFunction("sync")]
            [Throttle(10, {|#0:"1 minute"|})]
            [Debounce({|#1:"1.5s"|}, Timeout = {|#2:"10x"|})]
            [Timeouts(Start = {|#3:""|})]
            [CancelOn("sync/cancelled", Timeout = {|#4:"1H"|})]
            [EventTrigger("sync/requested")]
            public sealed class Sync : InngestFunction<JsonElement>
            {
                public override Task<object?> ExecuteAsync(FunctionContext<JsonElement> context, CancellationToken cancellationToken) => Task.FromResult<object?>(null);
            }
            """;

        await Verify(
            source,
            Time(0, "Period", "Throttle", "1 minute", NotATimeString),
            Time(1, "Period", "Debounce", "1.5s", NotATimeString),
            Time(2, "Timeout", "Debounce", "10x", NotATimeString),
            Time(3, "Start", "Timeouts", "", NotATimeString),
            Time(4, "Timeout", "CancelOn", "1H", NotATimeString));
    }

    [Fact]
    public async Task A_duration_outside_what_Inngest_allows_is_reported()
    {
        const string source = """
            [InngestFunction("sync")]
            [Throttle(10, period: {|#0:"0s"|})]
            [RateLimit(5, {|#1:"2d"|})]
            [Debounce({|#2:"8d"|}, Timeout = {|#3:"0s"|})]
            [BatchEvents(10, {|#4:"0s"|})]
            [Timeouts(Finish = {|#5:"0m"|})]
            [CronTrigger("0 9 * * *", Jitter = {|#6:"10m"|})]
            public sealed class Sync : InngestFunction<JsonElement>
            {
                public override Task<object?> ExecuteAsync(FunctionContext<JsonElement> context, CancellationToken cancellationToken) => Task.FromResult<object?>(null);
            }
            """;

        await Verify(
            source,
            Time(0, "Period", "Throttle", "0s", "must be at least 1s"),
            Time(1, "Period", "RateLimit", "2d", "must be between 1s and 24h"),
            Time(2, "Period", "Debounce", "8d", "must be between 1s and 7d"),
            Time(3, "Timeout", "Debounce", "0s", "must be greater than zero"),
            Time(4, "Timeout", "BatchEvents", "0s", "must be at least 1s"),
            Time(5, "Finish", "Timeouts", "0m", "must be greater than zero"),
            Time(6, "Jitter", "CronTrigger", "10m", "must be between 1s and 5m"));
    }

    [Fact]
    public async Task Valid_time_strings_and_optional_ones_left_out_are_fine()
    {
        const string source = """
            [InngestFunction("sync")]
            [Throttle(10, "1m", Burst = 2)]
            [RateLimit(5, "24h")]
            [Debounce("7d")]
            [BatchEvents(10, "1w")]
            [Timeouts(Start = "1h30m", Finish = null)]
            [CancelOn("sync/cancelled")]
            [EventTrigger("sync/requested")]
            public sealed class Sync : InngestFunction<JsonElement>
            {
                public override Task<object?> ExecuteAsync(FunctionContext<JsonElement> context, CancellationToken cancellationToken) => Task.FromResult<object?>(null);
            }
            """;

        await Verify(source);
    }

    // INNGEST033

    [Fact]
    public async Task An_event_trigger_for_another_event_than_the_data_type_names_is_reported()
    {
        const string source = Events + """
            [InngestFunction("welcome")]
            [EventTrigger({|#0:"user/deleted"|})]
            [{|#1:EventTrigger<OrderPaid>|}]
            public sealed class Welcome : InngestFunction<UserSignedUp>
            {
                public override Task<object?> ExecuteAsync(FunctionContext<UserSignedUp> context, CancellationToken cancellationToken) => Task.FromResult<object?>(null);
            }
            """;

        await Verify(source, Mismatch(0, "user/deleted"), Mismatch(1, "shop/order.paid"));
    }

    [Fact]
    public async Task A_function_with_one_trigger_for_its_event_is_fine()
    {
        const string source = Events + """
            [InngestFunction("welcome")]
            [EventTrigger("user/signed-up")]
            [EventTrigger("user/imported")]
            public sealed class Welcome : InngestFunction<UserSignedUp>
            {
                public override Task<object?> ExecuteAsync(FunctionContext<UserSignedUp> context, CancellationToken cancellationToken) => Task.FromResult<object?>(null);
            }

            [InngestFunction("audit")]
            [EventTrigger("user/*")]
            public sealed class Audit : InngestFunction<UserSignedUp, string>
            {
                public override Task<string> ExecuteAsync(FunctionContext<UserSignedUp> context, CancellationToken cancellationToken) => Task.FromResult("");
            }

            [InngestFunction("typed")]
            [EventTrigger<UserSignedUp>]
            public sealed class Typed : InngestFunction<UserSignedUp>
            {
                public override Task<object?> ExecuteAsync(FunctionContext<UserSignedUp> context, CancellationToken cancellationToken) => Task.FromResult<object?>(null);
            }

            [InngestFunction("untyped")]
            [EventTrigger("anything")]
            public sealed class Untyped : InngestFunction<JsonElement>
            {
                public override Task<object?> ExecuteAsync(FunctionContext<JsonElement> context, CancellationToken cancellationToken) => Task.FromResult<object?>(null);
            }

            [InngestFunction("computed")]
            [EventTrigger("computed/other")]
            public sealed class Computed : InngestFunction<ComputedName>
            {
                public override Task<object?> ExecuteAsync(FunctionContext<ComputedName> context, CancellationToken cancellationToken) => Task.FromResult<object?>(null);
            }

            public sealed record ComputedName : IInngestEventData
            {
                public static string EventName => Names.Computed;
            }

            public static class Names
            {
                public const string Computed = "computed/name";
            }
            """;

        await Verify(source);
    }

    private const string Events = """
        public sealed record UserSignedUp(string Email) : IInngestEventData
        {
            public static string EventName => "user/signed-up";
        }

        public sealed record OrderPaid(string OrderId) : IInngestEventData
        {
            public static string EventName { get; } = "shop/order.paid";
        }

        """;

    private static DiagnosticResult Cron(int markup, string schedule, string problem) =>
        new DiagnosticResult(DiagnosticDescriptors.InvalidCronSchedule).WithLocation(markup).WithArguments(schedule, problem);

    private static DiagnosticResult Time(int markup, string property, string attribute, string value, string problem) =>
        new DiagnosticResult(DiagnosticDescriptors.InvalidTimeString).WithLocation(markup).WithArguments(property, attribute, value, problem);

    private static DiagnosticResult Mismatch(int markup, string trigger) =>
        new DiagnosticResult(DiagnosticDescriptors.EventNameMismatch).WithLocation(markup).WithArguments("Welcome", "UserSignedUp", "user/signed-up", trigger);

    private static async Task Verify(string source, params DiagnosticResult[] expected)
    {
        var test = new InngestAnalyzerTest<FunctionOptionsAnalyzer>(Sources.Usings + source);
        test.ExpectedDiagnostics.AddRange(expected);
        await test.RunAsync();
    }
}
