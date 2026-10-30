using DotNet.Testcontainers.Builders;
using Inngest.IntegrationTests.Harness;
using Inngest.Testing.Containers;

namespace Inngest.IntegrationTests.Containers;

public sealed class StartFailureTests
{
    [Fact(Timeout = 90_000)]
    public async Task A_server_that_does_not_start_fails_with_its_own_log()
    {
        // The server stops at once on an option it does not know. The wait is shortened, since it has to give up.
        var wait = Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
            request => request.ForPort(InngestBuilder.InngestPort).ForPath("/health"),
            strategy => strategy.WithTimeout(TimeSpan.FromSeconds(20)));
        await using var server = new InngestBuilder(InngestServer.Image).WithCommand("--no-such-option").WithWaitStrategy(wait).Build();

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => server.StartAsync(TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("did not become ready");
        exception.Message.ShouldContain("no-such-option");
        exception.InnerException.ShouldNotBeNull();
    }
}
