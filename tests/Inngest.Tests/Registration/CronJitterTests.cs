using Inngest.Functions;
using static Inngest.Tests.Registration.RegistryFixtures;

namespace Inngest.Tests.Registration;

// Jitter is a duration string next to the schedule, as in the TypeScript SDK's config, and Inngest accepts 1s to 5m.
public sealed class CronJitterTests
{
    [Theory]
    [InlineData(1, "1s")]
    [InlineData(90, "1m30s")]
    [InlineData(300, "5m")]
    public void Jitter_is_written_next_to_the_schedule(int seconds, string expected) =>
        Property(Orders with { Triggers = [Trigger.Cron("0 9 * * *", TimeSpan.FromSeconds(seconds))] }, "triggers")
            .ShouldBe($$"""[{"cron":"0 9 * * *","jitter":"{{expected}}"}]""");

    [Fact]
    public void Jitter_is_written_with_a_time_zone_prefix_on_the_schedule() =>
        Property(Orders with { Triggers = [Trigger.Cron("0 9 * * 1-5", TimeSpan.FromMinutes(2), "Europe/Prague")] }, "triggers")
            .ShouldBe("""[{"cron":"TZ=Europe/Prague 0 9 * * 1-5","jitter":"2m"}]""");

    [Fact]
    public void A_schedule_without_jitter_has_no_jitter_field() =>
        Property(Orders with { Triggers = [Trigger.Cron("0 9 * * *"), new CronTrigger("0 18 * * *") { Jitter = TimeSpan.FromSeconds(30) }] }, "triggers")
            .ShouldBe("""[{"cron":"0 9 * * *"},{"cron":"0 18 * * *","jitter":"30s"}]""");

    [Fact]
    public void The_jitter_overload_keeps_the_time_zone() =>
        Trigger.Cron("0 9 * * *", TimeSpan.FromMinutes(1), "UTC")
            .ShouldBe(new CronTrigger("0 9 * * *") { Jitter = TimeSpan.FromMinutes(1), Timezone = "UTC" });

    [Theory]
    [InlineData(1)]
    [InlineData(60)]
    [InlineData(300)]
    public void Jitter_from_one_second_to_five_minutes_is_accepted(int seconds) =>
        Should.NotThrow(() => FunctionOptionsValidator.Validate(Orders with { Triggers = [Trigger.Cron("0 9 * * *", TimeSpan.FromSeconds(seconds))] }));

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(500, "0.5s")]
    [InlineData(-1000, "-1s")]
    [InlineData(301_000, "5m1s")]
    [InlineData(3_600_000, "1h")]
    public void Jitter_outside_one_second_to_five_minutes_is_rejected(int milliseconds, string shown)
    {
        var options = Orders with { Triggers = [Trigger.Event("order/created"), Trigger.Cron("0 9 * * *", TimeSpan.FromMilliseconds(milliseconds))] };

        var exception = Should.Throw<InvalidOperationException>(() => FunctionOptionsValidator.Validate(options));

        exception.Message.ShouldBe($"Function \"orders\": The jitter of cron trigger \"0 9 * * *\" must be between 1s and 5m, but was {shown}.");
    }
}
