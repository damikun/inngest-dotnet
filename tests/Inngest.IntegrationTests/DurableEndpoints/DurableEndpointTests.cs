using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.DurableEndpoints;

[CollectionDefinition(Name)]
public sealed class DurableEndpointCollection : ICollectionFixture<DurableEndpointFixture>
{
    public const string Name = "DurableEndpoints";
}

// Durable endpoints against the dev server: the run a finished request records, and the round trip when a step waits
// or fails. Inngest takes the run over, calls the endpoint back at the domain the run recorded with the original body
// and query, and the client that followed the redirect gets the endpoint's final response from the output URL.
[Collection(DurableEndpointCollection.Name)]
public sealed class DurableEndpointTests(DurableEndpointFixture fixture) : IntegrationTest<DurableEndpointFixture>(fixture)
{
    // The client sees each redirect itself; the output URL is on the dev server.
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(2) };

    [Fact(Timeout = 60_000)]
    public async Task A_finished_request_answers_directly_and_its_run_completes_in_Inngest()
    {
        using var response = await PostAsync($"/orders/{TestId}", """{"note":"gift"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var order = await response.Content.ReadFromJsonAsync<JsonElement>(Cancellation);
        order.GetProperty("total").GetInt32().ShouldBe(42);
        order.GetProperty("note").GetString().ShouldBe("gift");
        var runId = response.Headers.GetValues("X-Run-ID").Single();

        // The REST API cannot load a run that was created and finished in one checkpoint, so the trace shows it.
        var trace = await Api.WaitForTraceAsync(runId, "the run completed", trace => trace.Status == "COMPLETED", Cancellation);
        trace.Spans.ShouldContain(span => span.StepOp == "RUN" && span.Status == "COMPLETED");
        Fixture.Attempts(TestId, "price").ShouldBe(1);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_sleep_hands_the_run_over_and_the_resumed_call_gets_the_original_body_and_query()
    {
        const string body = """{"orderId":"123","note":"spike"}""";

        using var response = await PostAsync($"/ship/{TestId}?source=test&n=1", body);

        response.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        var output = await FollowAsync(response);
        output.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var shipped = JsonDocument.Parse(output.Body).RootElement;
        shipped.GetProperty("body").GetString().ShouldBe(body);
        shipped.GetProperty("query").GetString().ShouldBe("?source=test&n=1");
        shipped.GetProperty("packed").GetInt32().ShouldBe(1);
        Fixture.Attempts(TestId, "pack").ShouldBe(1);
        Fixture.Attempts(TestId, "after-sleep").ShouldBe(1);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_failed_step_is_retried_by_Inngest_and_the_client_gets_the_result()
    {
        using var response = await PostAsync($"/flaky/{TestId}", "{}");

        response.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        var output = await FollowAsync(response);
        output.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = JsonDocument.Parse(output.Body).RootElement;
        result.GetProperty("reserved").GetString().ShouldBe("reserved");
        result.GetProperty("attempts").GetInt32().ShouldBe(2);
    }

    [Fact(Timeout = 60_000)]
    public async Task An_endpoint_that_throws_is_retried_from_the_start()
    {
        using var response = await PostAsync($"/unstable/{TestId}", "{}");

        response.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        var output = await FollowAsync(response);
        output.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonDocument.Parse(output.Body).RootElement.GetProperty("attempts").GetInt32().ShouldBe(2);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_step_that_fails_for_good_comes_back_to_the_endpoint_as_a_step_failure()
    {
        using var response = await PostAsync($"/refused/{TestId}", "{}");

        response.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        var output = await FollowAsync(response);
        output.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonDocument.Parse(output.Body).RootElement.GetProperty("error").GetString().ShouldBe("card declined");
        Fixture.Attempts(TestId, "charge").ShouldBe(1);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_wait_for_an_event_ends_when_the_event_arrives()
    {
        using var response = await PostAsync($"/approve/{TestId}", "{}");
        response.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        var runId = response.Headers.GetValues("X-Run-ID").Single();
        await Poll.UntilAsync(
            $"run {runId} to be running",
            token => Api.GetRunAsync(runId, token),
            run => run.Status == RunStatus.Running,
            TimeSpan.FromSeconds(15),
            Fixture.DescribeAsync,
            Cancellation);

        await Api.SendEventAsync("test/approved", new { testId = TestId, by = "ops" }, Cancellation);

        var output = await FollowAsync(response);
        output.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonDocument.Parse(output.Body).RootElement.GetProperty("approvedBy").GetString().ShouldBe("ops");
    }

    private Task<HttpResponseMessage> PostAsync(string pathAndQuery, string body) =>
        Client.PostAsync(new Uri(Fixture.AppUrl, pathAndQuery), new StringContent(body, Encoding.UTF8, "application/json"), Cancellation);

    // The output URL waits until the run has ended and then replays the endpoint's response.
    // The dev server answers the output URL once the run's spans show it ended, with the output of its trace run, which
    // is stored apart from the spans. Asked at that moment it finds no output yet and answers 200 with no body, so an empty
    // 200 is asked again. Every endpoint these tests follow answers with a body.
    private static async Task<(HttpStatusCode StatusCode, string Body)> FollowAsync(HttpResponseMessage redirect)
    {
        var location = redirect.Headers.Location.ShouldNotBeNull();
        location.AbsolutePath.ShouldContain("/v1/http/runs/");
        for (var attempt = 1; ; attempt++)
        {
            using var output = await Client.GetAsync(location, Cancellation);
            var body = await output.Content.ReadAsStringAsync(Cancellation);
            if (output.StatusCode != HttpStatusCode.OK || body.Length > 0 || attempt == 50)
            {
                return (output.StatusCode, body);
            }

            await Task.Delay(100, Cancellation);
        }
    }
}
