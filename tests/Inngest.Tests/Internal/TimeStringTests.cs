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

    [Fact]
    public void Formats_instants_as_utc_rfc3339() =>
        TimeString.Format(new DateTimeOffset(2026, 10, 28, 20, 15, 30, 250, TimeSpan.FromHours(1)))
            .ShouldBe("2026-10-28T19:15:30.250Z");
}
