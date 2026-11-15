using Inngest.Execution;
using Microsoft.Extensions.Logging.Abstractions;

namespace Inngest.Tests.Execution;

// The runs of durable endpoint requests, recorded in the background after their response.
public sealed class EndpointRunSavesTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_token_that_ends_hand_overs_is_cancelled_once_the_app_has_stopped()
    {
        var saves = new EndpointRunSaves(NullLogger<EndpointRunSaves>.Instance);

        await saves.StopAsync(CancellationToken);
        saves.Stopped.IsCancellationRequested.ShouldBeFalse();
        await saves.StoppedAsync(CancellationToken);

        saves.Stopped.IsCancellationRequested.ShouldBeTrue();
    }
}
