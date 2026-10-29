using Inngest.Internal;

namespace Inngest.Tests.Internal;

public sealed class TimeStringTests
{
    [Theory]
    [InlineData(0, "0s")]
    [InlineData(1, "1s")]
    [InlineData(999, "1s")]
    [InlineData(1_000, "1s")]
    [InlineData(1_500, "1s")]
    [InlineData(60_000, "1m")]
    [InlineData(5_400_000, "1h30m")]
    [InlineData(86_400_000, "1d")]
    [InlineData(694_861_000, "1w1d1h1m1s")]
    public void Formats_durations_like_the_typescript_sdk(long milliseconds, string expected) =>
        TimeString.Format(TimeSpan.FromMilliseconds(milliseconds)).ShouldBe(expected);

    [Fact]
    public void Rejects_negative_durations() =>
        Should.Throw<ArgumentOutOfRangeException>(() => TimeString.Format(TimeSpan.FromSeconds(-1)));

    [Theory]
    [InlineData("0s", 0)]
    [InlineData("30s", 30)]
    [InlineData("5m", 300)]
    [InlineData("1h30m", 5_400)]
    [InlineData("1d", 86_400)]
    [InlineData("1w1d1h1m1s", 694_861)]
    [InlineData("90s", 90)]
    [InlineData("1m1h", 3_660)]
    public void Parses_time_strings(string text, long seconds)
    {
        TimeString.TryParse(text, out var duration).ShouldBeTrue();
        duration.ShouldBe(TimeSpan.FromSeconds(seconds));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("30")]
    [InlineData("s")]
    [InlineData("1.5h")]
    [InlineData("-5s")]
    [InlineData("5 m")]
    [InlineData("5M")]
    [InlineData("100ms")]
    [InlineData("00:00:30")]
    [InlineData("99999999999999999999s")]
    [InlineData("9223372036854775807w")]
    public void Rejects_what_is_not_a_time_string(string? text)
    {
        TimeString.TryParse(text, out var duration).ShouldBeFalse();
        duration.ShouldBe(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(59)]
    [InlineData(5_400)]
    [InlineData(694_861)]
    public void Parses_what_it_formats(long seconds)
    {
        var duration = TimeSpan.FromSeconds(seconds);

        TimeString.TryParse(TimeString.Format(duration), out var parsed).ShouldBeTrue();
        parsed.ShouldBe(duration);
    }

    [Fact]
    public void Formats_instants_as_utc_rfc3339() =>
        TimeString.Format(new DateTimeOffset(2026, 10, 28, 20, 15, 30, 250, TimeSpan.FromHours(1)))
            .ShouldBe("2026-10-28T19:15:30.250Z");
}
