using Inngest.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;

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

    // In a host whose web server stops after this service, as when the web host is set up before AddInngest, a request
    // the server lets finish starts its save once StopAsync has returned. It is waited for once the app has stopped.
    [Fact]
    public async Task A_save_started_after_the_service_stopped_is_waited_for_once_the_app_has_stopped()
    {
        var logger = new FakeLogger<EndpointRunSaves>();
        var saves = new EndpointRunSaves(logger);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = false;
        await saves.StopAsync(CancellationToken);

        saves.Start("run-1", "POST /orders/{id}", async _ =>
        {
            await release.Task;
            saved = true;
        }).IsCompleted.ShouldBeTrue();
        var stopped = saves.StoppedAsync(CancellationToken);

        stopped.IsCompleted.ShouldBeFalse();
        saves.Stopped.IsCancellationRequested.ShouldBeFalse();
        release.SetResult();
        await stopped.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken);
        saved.ShouldBeTrue();
        saves.Stopped.IsCancellationRequested.ShouldBeTrue();
        logger.Collector.Count.ShouldBe(0);
    }

    [Fact]
    public async Task A_save_started_after_the_app_has_stopped_does_not_run_and_is_logged_as_not_recorded()
    {
        var logger = new FakeLogger<EndpointRunSaves>();
        var saves = new EndpointRunSaves(logger);
        await saves.StopAsync(CancellationToken);
        await saves.StoppedAsync(CancellationToken);
        var ran = false;

        var save = saves.Start("run-1", "POST /orders/{id}", _ =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        save.IsCompleted.ShouldBeTrue();
        ran.ShouldBeFalse();
        var warning = logger.Collector.GetSnapshot().ShouldHaveSingleItem();
        warning.Id.Id.ShouldBe(153);
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldContain("run-1");
        warning.Message.ShouldContain("POST /orders/{id}");
    }
}
