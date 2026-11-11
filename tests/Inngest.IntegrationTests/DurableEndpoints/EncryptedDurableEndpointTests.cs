using System.Net;
using System.Text;
using System.Web;

namespace Inngest.IntegrationTests.DurableEndpoints;

[CollectionDefinition(Name)]
public sealed class EncryptedDurableEndpointCollection : ICollectionFixture<EncryptedDurableEndpointFixture>
{
    public const string Name = "EncryptedDurableEndpoints";
}

// A durable endpoint of an app that encrypts what it sends Inngest. The resumed run's output reaches Inngest encrypted,
// so the output URL of Inngest cannot replay it, and the app's output proxy decrypts it and replays it.
[Collection(EncryptedDurableEndpointCollection.Name)]
public sealed class EncryptedDurableEndpointTests(EncryptedDurableEndpointFixture fixture) : IntegrationTest<EncryptedDurableEndpointFixture>(fixture)
{
    [Fact(Timeout = 60_000)]
    public async Task The_output_proxy_decrypts_the_output_and_replays_the_endpoints_response()
    {
        using var response = await DurableEndpointTests.Client.PostAsync(
            new Uri(Fixture.AppUrl, $"/secret/{TestId}"),
            new StringContent("{}", Encoding.UTF8, "application/json"),
            Cancellation);

        response.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        var location = response.Headers.Location.ShouldNotBeNull();
        var output = await DurableEndpointTests.ReadOutputAsync(location);
        output.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        output.ContentType.ShouldStartWith("text/plain");
        output.Body.ShouldBe($"{TestId}: {EncryptedDurableEndpointFixture.Secret}");

        // What Inngest itself answers for the run holds nothing readable.
        var query = HttpUtility.ParseQueryString(location.Query);
        var inngestUrl = new Uri(Fixture.Server.Url, $"/v1/http/runs/{query["runId"]}/output?token={Uri.EscapeDataString(query["token"]!)}");
        var direct = await DurableEndpointTests.ReadOutputAsync(inngestUrl);
        direct.Body.ShouldNotContain(EncryptedDurableEndpointFixture.Secret);
        direct.Body.ShouldContain("__ENCRYPTED__");
    }

    [Fact(Timeout = 60_000)]
    public async Task A_request_that_finishes_at_once_answers_directly_and_its_encrypted_run_completes_in_Inngest()
    {
        using var response = await DurableEndpointTests.Client.PostAsync(
            new Uri(Fixture.AppUrl, $"/secret-now/{TestId}"),
            new StringContent("{}", Encoding.UTF8, "application/json"),
            Cancellation);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Cancellation)).ShouldBe($"{TestId}: {EncryptedDurableEndpointFixture.Secret}");
        var runId = response.Headers.GetValues("X-Run-ID").Single();

        var trace = await Api.WaitForTraceAsync(runId, "the run completed", trace => trace.Status == "COMPLETED", Cancellation);
        trace.Spans.ShouldContain(span => span.StepOp == "RUN" && span.Status == "COMPLETED");
    }
}
