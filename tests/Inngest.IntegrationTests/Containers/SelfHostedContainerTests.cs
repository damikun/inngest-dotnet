using System.Net;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.Containers;

public sealed class SelfHostedContainerTests(SelfHostedContainerFixture fixture)
    : ContainerRunTests<SelfHostedContainerFixture>(fixture), IClassFixture<SelfHostedContainerFixture>
{
    [Fact(Timeout = 60_000)]
    public void The_container_has_the_keys_the_builder_generated()
    {
        Fixture.Server.IsDevServer.ShouldBeFalse();
        Fixture.Server.SigningKey.ShouldNotBeNull().ShouldMatch("^[0-9a-f]{64}$");
        Fixture.Server.EventKey.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact(Timeout = 60_000)]
    public async Task Every_call_the_server_makes_is_signed_with_the_signing_key_and_so_is_the_answer()
    {
        var eventId = await Fixture.Api.SendEventAsync("containers/steps", new { value = 1 }, Cancellation);
        await Fixture.Api.WaitForRunEndAsync(eventId, Cancellation);

        var calls = Fixture.App.Requests.Snapshot().Where(request => request.Method == "POST").ToList();

        calls.ShouldNotBeEmpty();
        calls.ShouldAllBe(request => request.Signed && request.ResponseSigned);
    }

    [Fact(Timeout = 60_000)]
    public void The_sync_request_of_the_container_carries_a_valid_signature()
    {
        var sync = Fixture.App.Requests.Snapshot().First(request => request.Method == "PUT");

        sync.Signed.ShouldBeTrue();
        Signatures.IsValid(sync.Signature, sync.Body, Fixture.Server.SigningKey!).ShouldBeTrue();
    }

    [Fact(Timeout = 60_000)]
    public async Task The_server_wants_the_signing_key_on_its_rest_api()
    {
        using var anonymous = new HttpClient { BaseAddress = Fixture.Server.GetBaseAddress() };
        using var response = await anonymous.GetAsync($"v1/apps/{ContainerApp.AppId}/functions", Cancellation);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await Should.NotThrowAsync(() => Fixture.Api.GetAppFunctionsAsync(ContainerApp.AppId, Cancellation));
    }

    [Fact(Timeout = 60_000)]
    public async Task The_server_refuses_events_sent_with_another_event_key()
    {
        using var other = new DevServerClient(Fixture.Server.GetBaseAddress(), "not-the-event-key", Fixture.Server.SigningKey, Fixture.DescribeAsync);

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => other.SendEventAsync("containers/hello", new { name = "Mallory" }, Cancellation));

        exception.Message.ShouldContain("401");
    }
}
