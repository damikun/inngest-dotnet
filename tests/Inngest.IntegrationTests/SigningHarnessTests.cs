using System.Net;
using System.Security.Cryptography;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests;

// Checks of the signing fixture itself, independent of the SDK under test.
[Collection(SigningCollection.Name)]
public sealed class SigningHarnessTests(SigningFixture fixture) : IntegrationTest<SigningFixture>(fixture)
{
    public override ValueTask InitializeAsync() => ValueTask.CompletedTask;

    [Fact(Timeout = 30_000)]
    public async Task Self_hosted_server_signs_its_sync_request_with_the_signing_key()
    {
        var sync = await Poll.UntilAsync(
            "the self-hosted server's sync request to reach the app",
            _ => Fixture.App.Requests.Snapshot().FirstOrDefault(request => request.Method == "PUT"),
            request => request is not null,
            TimeSpan.FromSeconds(10),
            Cancellation);

        sync!.ServerKind.ShouldBe("cloud");
        Signatures.IsValid(sync.Signature, sync.Body, Fixture.SigningKey).ShouldBeTrue();
        Signatures.IsValid(sync.Signature, sync.Body, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))).ShouldBeFalse();
    }

    [Fact(Timeout = 30_000)]
    public async Task Self_hosted_server_wants_the_signing_key_on_its_api()
    {
        using var anonymous = new HttpClient { BaseAddress = Api.BaseUrl };
        using var response = await anonymous.GetAsync($"v1/apps/{Fixture.AppId}/functions", Cancellation);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await Should.NotThrowAsync(() => Api.GetAppFunctionsAsync(Fixture.AppId, Cancellation));
    }

    [Fact(Timeout = 30_000)]
    public async Task Self_hosted_server_accepts_events_sent_with_its_event_key()
    {
        var id = await Api.SendEventAsync("harness/ping", new { key = TestId }, Cancellation);

        id.Length.ShouldBe(26);
    }
}
