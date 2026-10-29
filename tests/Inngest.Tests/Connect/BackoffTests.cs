using Inngest.Connect;

namespace Inngest.Tests.Connect;

public sealed class BackoffTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 5)]
    [InlineData(4, 10)]
    [InlineData(5, 20)]
    [InlineData(6, 30)]
    [InlineData(7, 60)]
    [InlineData(8, 120)]
    [InlineData(9, 300)]
    [InlineData(10, 300)]
    [InlineData(1000, 300)]
    public void Follows_the_schedule_of_the_typescript_sdk(int failures, int seconds)
    {
        Backoff.AfterFailures(failures).ShouldBe(TimeSpan.FromSeconds(seconds));
    }

    [Fact]
    public void A_draining_gateway_is_retried_within_a_second_and_a_half()
    {
        for (var i = 0; i < 100; i++)
        {
            Backoff.GatewayDraining().ShouldBeInRange(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1500));
        }
    }
}
