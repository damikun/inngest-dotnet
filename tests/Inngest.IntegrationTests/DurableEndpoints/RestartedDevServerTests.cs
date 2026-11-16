using System.Diagnostics;
using System.Net;
using System.Text;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.DurableEndpoints;

[CollectionDefinition(Name)]
public sealed class RestartedDevServerCollection : ICollectionFixture<RestartedDevServerFixture>
{
    public const string Name = "RestartedDevServer";
}

// A dev server without --persist forgets the endpoints it stored when it restarts, while the app keeps the time of its
// first run of each. The dev server syncs the app when it comes back, which Inngest takes as a change, so the app
// waits for the new one to store an endpoint before it adds the steps that hand its first run over.
[Collection(RestartedDevServerCollection.Name)]
public sealed class RestartedDevServerTests(RestartedDevServerFixture fixture) : IntegrationTest<RestartedDevServerFixture>(fixture)
{
    // The wait is a second from the call that creates the run, which the hand-over cannot take less than. Without it
    // the hand-over takes a few milliseconds.
    private static readonly TimeSpan Waited = TimeSpan.FromMilliseconds(900);

    [Fact(Timeout = 120_000)]
    public async Task A_hand_over_after_the_dev_server_restarts_waits_for_it_to_store_the_endpoint_again()
    {
        (await ShipAsync()).ShouldBeGreaterThan(Waited);
        var synced = Syncs();

        await Fixture.RestartServerAsync(Cancellation);
        await Poll.UntilAsync("the new dev server to sync the app", _ => Syncs(), count => count > synced, TimeSpan.FromSeconds(30), Cancellation);

        (await ShipAsync()).ShouldBeGreaterThan(Waited);
    }

    // Logged by the app for a sync that Inngest takes as a change.
    private int Syncs() => Fixture.App.Logs.GetSnapshot().Count(record => record.Id.Id == 406);

    // How long the hand-over took, once its run has completed.
    private async Task<TimeSpan> ShipAsync()
    {
        var started = Stopwatch.GetTimestamp();
        using var response = await DurableEndpointTests.Client.PostAsync(
            new Uri(Fixture.AppUrl, "/ship"),
            new StringContent("{}", Encoding.UTF8, "application/json"),
            Cancellation);
        var took = Stopwatch.GetElapsedTime(started);

        response.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        var runId = response.Headers.GetValues("X-Run-ID").Single();
        await Poll.UntilAsync(
            $"run {runId} to complete",
            token => Api.GetRunAsync(runId, token),
            run => run.Status == RunStatus.Completed && run.HasEnded,
            TimeSpan.FromSeconds(30),
            Fixture.DescribeAsync,
            Cancellation);
        return took;
    }
}
