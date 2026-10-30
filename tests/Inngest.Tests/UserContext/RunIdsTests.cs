using Inngest.UserContext;

namespace Inngest.Tests.UserContext;

// The time a run was created, which is what its id says when Inngest made it.
public sealed class RunIdsTests
{
    [Theory]
    [InlineData("2026-10-29T10:00:00.000Z")]
    [InlineData("2024-01-18T14:01:44.123Z")]
    [InlineData("1970-01-01T00:00:00.000Z")]
    public void The_time_of_a_ulid_is_its_first_ten_characters(string time)
    {
        var created = DateTimeOffset.Parse(time, System.Globalization.CultureInfo.InvariantCulture);

        RunIds.StartOf(UserContextApp.RunIdAt(created)).ShouldBe(created);
    }

    [Fact]
    public void A_ulid_from_inngest_is_read_as_it_is_written()
    {
        // The id of a run from the SDK's evaluation against the dev server, created on 2025-10-24.
        RunIds.StartOf("01K8A2WQ7M9Z3T5V0XJ4B6N8CD").ShouldNotBeNull().Year.ShouldBe(2025);
        RunIds.StartOf("01k8a2wq7m9z3t5v0xj4b6n8cd").ShouldBe(RunIds.StartOf("01K8A2WQ7M9Z3T5V0XJ4B6N8CD"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("01JRUN")]
    [InlineData("01K8A2WQ7M9Z3T5V0XJ4B6N8C")]
    [InlineData("01K8A2WQ7M9Z3T5V0XJ4B6N8CDE")]
    [InlineData("01K8A2WQ7M9Z3T5V0XJ4B6N8CI")]
    [InlineData("01K8A2WQ7M9Z3T5V0XJ4B6N8CU")]
    [InlineData("01K8A2WQ7M9Z3T5V0XJ4B6N8C-")]
    [InlineData("81K8A2WQ7M9Z3T5V0XJ4B6N8CD")]
    [InlineData("run_0123456789abcdefghijklmn")]
    public void An_id_that_is_not_a_ulid_has_no_time(string? runId)
    {
        RunIds.StartOf(runId).ShouldBeNull();
    }
}
