using Inngest.Internal;

namespace Inngest.Tests.Internal;

// The cases follow parser.go of github.com/robfig/cron/v3, which Inngest v1.52.0 checks schedules with, created with
// the minute, hour, day of month, month and day of week fields and without descriptors.
public sealed class CronExpressionTests
{
    [Theory]
    [InlineData("* * * * *")]
    [InlineData("0 9 * * 1-5")]
    [InlineData("*/5 * * * *")]
    [InlineData("5/15 * * * *")]
    [InlineData("0-30/10 * * * *")]
    [InlineData("0 0 1,15 * *")]
    [InlineData("0 0 1,,15 * *")]
    [InlineData("0 0 ? * ?")]
    [InlineData("0 0 * JAN-dec Sun-Sat")]
    [InlineData("0 0 31 12 6")]
    [InlineData("+5 * * * *")]
    [InlineData("  0   9 *\t* *  ")]
    [InlineData("TZ=America/New_York 0 9 * * *")]
    [InlineData("CRON_TZ=UTC 0 9 * * *")]
    public void Accepts_what_Inngest_accepts(string schedule) => CronExpression.FindProblem(schedule).ShouldBeNull();

    [Theory]
    [InlineData("", "it is empty")]
    [InlineData("   ", "it has 0 fields, and Inngest expects 5: minute, hour, day of month, month and day of week")]
    [InlineData("* * * *", "it has 4 fields, and Inngest expects 5: minute, hour, day of month, month and day of week")]
    [InlineData("0 * * * * *", "it has 6 fields, and Inngest expects 5: minute, hour, day of month, month and day of week")]
    [InlineData("@hourly", "Inngest does not accept descriptors such as @hourly; write the five fields instead")]
    [InlineData("@every 5m", "Inngest does not accept descriptors such as @every; write the five fields instead")]
    [InlineData("60 * * * *", "the minute 60 is above 59")]
    [InlineData("* 24 * * *", "the hour 24 is above 23")]
    [InlineData("* * 0 * *", "the day of month 0 is below 1")]
    [InlineData("* * 32 * *", "the day of month 32 is above 31")]
    [InlineData("* * * 13 *", "the month 13 is above 12")]
    [InlineData("* * * * 7", "the day of week 7 is above 6")]
    [InlineData("* * * * mon-sun", "the day of week range \"mon-sun\" starts after it ends")]
    [InlineData("10-5 * * * *", "the minute range \"10-5\" starts after it ends")]
    [InlineData("1-2-3 * * * *", "the minute \"1-2-3\" has more than one hyphen")]
    [InlineData("*/5/2 * * * *", "the minute \"*/5/2\" has more than one slash")]
    [InlineData("*/0 * * * *", "the minute \"*/0\" has a step of 0")]
    [InlineData("*/x * * * *", "the step \"x\" of the minute is not a whole number")]
    [InlineData("a * * * *", "the minute \"a\" is not a whole number")]
    [InlineData("-1 * * * *", "the minute \"\" is not a whole number")]
    [InlineData("* * * foo *", "the month \"foo\" is not a whole number or a name such as jan")]
    [InlineData("* * * * 1.5", "the day of week \"1.5\" is not a whole number or a name such as sun")]
    [InlineData("L * * * *", "the minute \"L\" is not a whole number")]
    [InlineData("TZ=UTC", "the time zone is not followed by a space and the fields")]
    public void Rejects_what_Inngest_rejects(string schedule, string problem) => CronExpression.FindProblem(schedule).ShouldBe(problem);

    [Fact]
    public void Rejects_a_schedule_longer_than_255_characters() =>
        CronExpression.FindProblem("0 0 * * " + string.Join(",", Enumerable.Repeat("1", 125))).ShouldBe("it is longer than 255 characters");

    [Theory]
    [InlineData("0 9 * * *", null, "0 9 * * *")]
    [InlineData("0 9 * * *", " ", "0 9 * * *")]
    [InlineData("0 9 * * *", "Europe/Prague", "TZ=Europe/Prague 0 9 * * *")]
    [InlineData("TZ=UTC 0 9 * * *", "Europe/Prague", "TZ=UTC 0 9 * * *")]
    public void Puts_the_time_zone_in_front_unless_the_schedule_has_one(string schedule, string? timezone, string expected) =>
        CronExpression.WithTimezone(schedule, timezone).ShouldBe(expected);
}
