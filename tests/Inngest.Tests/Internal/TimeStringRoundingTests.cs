using Inngest.Internal;

namespace Inngest.Tests.Internal;

// Inngest waits and expires in whole seconds. These are the rounding rules of the TypeScript SDK's time strings.
public sealed class TimeStringRoundingTests
{
    [Theory]
    [InlineData(1_500, "1s")]
    [InlineData(65_500, "1m5s")]
    [InlineData(3_600_999, "1h")]
    [InlineData(60_000, "1m")]
    [InlineData(1_000, "1s")]
    public void Remainders_under_a_second_are_dropped_from_longer_durations(long milliseconds, string expected) =>
        TimeString.Format(TimeSpan.FromMilliseconds(milliseconds)).ShouldBe(expected);

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(500)]
    [InlineData(999)]
    public void A_positive_duration_under_a_second_rounds_up_to_one_second_and_never_to_nothing(long milliseconds) =>
        TimeString.Format(TimeSpan.FromMilliseconds(milliseconds)).ShouldBe("1s");

    [Fact]
    public void A_duration_of_a_single_tick_rounds_up_to_one_second() =>
        TimeString.Format(TimeSpan.FromTicks(1)).ShouldBe("1s");

    [Theory]
    [InlineData(30, "30s")]
    [InlineData(300, "5m")]
    [InlineData(7_200, "2h")]
    [InlineData(259_200, "3d")]
    [InlineData(604_800, "1w")]
    [InlineData(1_209_600, "2w")]
    [InlineData(5_400, "1h30m")]
    public void Durations_use_the_largest_units_and_the_same_text_whichever_way_they_are_written(int seconds, string expected)
    {
        TimeString.Format(TimeSpan.FromSeconds(seconds)).ShouldBe(expected);
        TimeString.Format(TimeSpan.FromMilliseconds(seconds * 1000L)).ShouldBe(expected);
    }

    [Fact]
    public void The_epoch_is_written_as_an_iso_time() =>
        TimeString.Format(DateTimeOffset.UnixEpoch).ShouldBe("1970-01-01T00:00:00.000Z");

    [Fact]
    public void An_instant_with_an_offset_is_converted_to_utc() =>
        TimeString.Format(new DateTimeOffset(1970, 1, 1, 2, 0, 0, TimeSpan.FromHours(2))).ShouldBe("1970-01-01T00:00:00.000Z");
}
