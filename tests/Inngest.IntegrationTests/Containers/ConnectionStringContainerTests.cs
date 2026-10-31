using Inngest.IntegrationTests.Harness;
using Inngest.Testing.Containers;

namespace Inngest.IntegrationTests.Containers;

public sealed class ConnectionStringContainerTests
{
    [Fact(Timeout = 90_000)]
    public async Task Every_container_of_one_builder_answers_with_its_own_address()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var builder = new InngestServerBuilder(InngestServer.Image);
        await using var first = builder.Build();
        await using var second = builder.Build();
        await first.StartAsync(cancellationToken);
        await second.StartAsync(cancellationToken);

        // The connection string is the address of the API. A provider shared by the containers would answer for the one
        // that started last.
        first.GetConnectionString().ShouldBe(first.GetBaseAddress().AbsoluteUri);
        second.GetConnectionString().ShouldBe(second.GetBaseAddress().AbsoluteUri);
        first.GetConnectionString().ShouldNotBe(second.GetConnectionString());
    }
}
