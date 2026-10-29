using Inngest.Connect;

namespace Inngest.Tests.Connect;

// The gateway writes its intervals with Go's time.Duration.String().
public sealed class GoDurationTests
{
    [Theory]
    [InlineData("10s", 10_000)]
    [InlineData("30s", 30_000)]
    [InlineData("1m30s", 90_000)]
    [InlineData("2m0s", 120_000)]
    [InlineData("1h0m0s", 3_600_000)]
    [InlineData("1.5s", 1_500)]
    [InlineData("500ms", 500)]
    [InlineData(".5s", 500)]
    [InlineData("1h2m3.5s", 3_723_500)]
    [InlineData("0s", 0)]
    [InlineData("0", 0)]
    [InlineData("+5s", 5_000)]
    [InlineData("-5s", -5_000)]
    public void Reads_go_durations(string text, double milliseconds)
    {
        GoDuration.TryParse(text, out var duration).ShouldBeTrue();

        duration.ShouldBe(TimeSpan.FromMilliseconds(milliseconds));
    }

    [Theory]
    [InlineData("1500us")]
    [InlineData("1500µs")]
    [InlineData("1500μs")]
    [InlineData("1500000ns")]
    public void Reads_sub_millisecond_units(string text)
    {
        GoDuration.TryParse(text, out var duration).ShouldBeTrue();

        duration.ShouldBe(TimeSpan.FromMicroseconds(1500));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("10")]
    [InlineData("s")]
    [InlineData(".s")]
    [InlineData("1d")]
    [InlineData("10 s")]
    [InlineData("1..5s")]
    [InlineData("-")]
    [InlineData("99999999999999h")]
    public void Rejects_what_go_rejects(string? text)
    {
        GoDuration.TryParse(text, out _).ShouldBeFalse();
    }
}
