using Inngest.Execution;
using Microsoft.Extensions.Logging.Abstractions;

namespace Inngest.Tests.Execution;

// The runs of durable endpoint requests, recorded in the background after their response.
public sealed class EndpointRunSavesTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // While Inngest is behind, saves would otherwise pile up, each with the steps of its run.
    [Fact]
    public async Task Past_the_most_saves_under_way_a_request_waits_for_its_own()
    {
        var saves = new EndpointRunSaves(NullLogger<EndpointRunSaves>.Instance) { MaxRunning = 2 };
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<CancellationToken, Task> held = _ => release.Task;

        var first = saves.Start("run-1", "POST /orders", held);
        var second = saves.Start("run-2", "POST /orders", held);
        var third = saves.Start("run-3", "POST /orders", held);

        first.IsCompleted.ShouldBeTrue();
        second.IsCompleted.ShouldBeTrue();
        third.IsCompleted.ShouldBeFalse();
        release.SetResult();
        await third.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken);
        await saves.StopAsync(CancellationToken);
    }

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
