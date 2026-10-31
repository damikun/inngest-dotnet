using Aspire.Hosting.Testing;

namespace Inngest.Aspire.Hosting.Tests.Containers;

// The start of a self-hosted server whose key inngest start would refuse, in an AppHost that runs.
public sealed class RefusedKeysTests
{
    [Fact]
    public async Task A_server_with_a_key_it_would_refuse_does_not_start_and_the_wait_for_it_ends_with_the_reason()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var builder = DistributedApplicationTestingBuilder.Create();

        // The key as Inngest Cloud writes it, with the prefix that inngest start does not take.
        var signingKey = builder.AddParameter("signing", "signkey-prod-0a1b2c3d4e5f6071", secret: true);
        var eventKey = builder.AddParameter("events", "event-key-1", secret: true);
        builder.AddInngestSelfHosted("inngest", signingKey, eventKey);
        await using var app = await builder.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);

        // The wait for a server that stays healthy would end with a TimeoutException after the start timeout, not with this.
        var failure = await Should.ThrowAsync<DistributedApplicationException>(
            () => AppHostFixture.WaitForHealthyAsync(app, "inngest", cancellationToken));

        failure.Message.ShouldContain("failed to start");
        app.ResourceNotifications.TryGetCurrentState("inngest", out var resource).ShouldBeTrue();
        resource!.Snapshot.State!.Text.ShouldBe(KnownResourceStates.FailedToStart);
    }
}
