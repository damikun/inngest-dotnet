using System.Net;
using System.Text;
using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Functions;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.AzureFunctions;

// The function that serves Inngest, with the built-in HTTP model of the worker, against a self-hosted server that signs
// every request. The app stands behind a stand-in for the Functions host, since a real host is not available here: it turns
// each request into the HttpRequestData a worker creates, calls the function, and sends back the HttpResponseData it returns.
[Collection(AzureFunctionsCollection.Name)]
public sealed class AzureFunctionsTests(AzureFunctionsFixture fixture) : IntegrationTest<AzureFunctionsFixture>(fixture)
{
    private static readonly HttpClient Http = new();

    [Fact(Timeout = 30_000)]
    public async Task The_server_syncs_the_app_through_the_function_and_learns_the_framework_and_the_url()
    {
        var app = (await Api.GetAppsAsync(Cancellation)).Single(app => app.ExternalId == Fixture.AppId);

        app.Error.ShouldBeNull();
        app.Framework.ShouldBe("azure-functions");
        app.FunctionCount.ShouldBe(1);

        // The URL is the one the server called, with the route of the function.
        app.Url.ShouldBe(Fixture.App.ServerFacingUrl.AbsoluteUri);
        var sync = Fixture.App.Requests.Snapshot().First(request => request.Method == "PUT");
        sync.Signed.ShouldBeTrue();
        sync.StatusCode.ShouldBe(200);
        sync.ResponseSigned.ShouldBeTrue();
    }

    [Fact(Timeout = 60_000)]
    public async Task A_run_with_steps_completes_over_signed_calls_and_every_answer_is_signed()
    {
        var eventId = await Api.SendEventAsync(AzureFunctionsFunctions.Greet, new GreetEvent(TestId, "Prague"), Cancellation);

        var run = await Api.WaitForRunEndAsync(eventId, Cancellation);

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<TextResult>().ShouldBe(new TextResult("HELLO, PRAGUE!"));
        Observed.Names.ShouldBe(["compose", "shout"]);

        // One call for each step and one that returns the output, each answered with a signature over its body.
        var calls = Fixture.App.Requests.Snapshot()
            .Where(request => request.Method == "POST" && request.Target.Contains("fnId=azfn-greet", StringComparison.Ordinal))
            .ToList();
        calls.Count.ShouldBeGreaterThanOrEqualTo(3);
        calls.ShouldAllBe(call => call.Signed && call.ResponseSigned && (call.StatusCode == 200 || call.StatusCode == 206));
    }

    [Fact(Timeout = 30_000)]
    public async Task A_call_without_a_signature_is_refused_and_no_function_runs()
    {
        using var content = new StringContent("""{"event":{"name":"azfn/greet","data":{}},"steps":{},"ctx":{},"version":2}""", Encoding.UTF8, "application/json");

        using var response = await Http.PostAsync(new Uri(Fixture.App.LocalUrl, $"{Fixture.App.LocalUrl.AbsolutePath}?fnId=azfn-greet&stepId=step"), content, Cancellation);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.Contains("X-Inngest-SDK").ShouldBeFalse();
        Observed.Names.ShouldBeEmpty();
    }
}

// The same function with the ASP.NET Core integration of the worker and the dev server, with streaming on.
[Collection(AzureFunctionsStreamingCollection.Name)]
public sealed class AzureFunctionsStreamingTests(AzureFunctionsStreamingFixture fixture) : IntegrationTest<AzureFunctionsStreamingFixture>(fixture)
{
    [Fact(Timeout = 30_000)]
    public async Task The_server_syncs_the_app_through_the_function()
    {
        var app = (await Api.GetAppsAsync(Cancellation)).Single(app => app.ExternalId == Fixture.AppId);

        app.Error.ShouldBeNull();
        app.Framework.ShouldBe("azure-functions");
        app.FunctionCount.ShouldBe(2);
    }

    [Fact(Timeout = 90_000)]
    public async Task A_call_longer_than_three_seconds_is_streamed_and_Inngest_reads_its_result()
    {
        var eventId = await Api.SendEventAsync(AzureFunctionsFunctions.Slow, new RunEvent(TestId), Cancellation);

        var run = await Api.WaitForRunEndAsync(eventId, Cancellation, TimeSpan.FromSeconds(60));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<TextResult>().ShouldBe(new TextResult("finished after a long step"));
        var statuses = Fixture.App.Requests.Snapshot()
            .Where(request => request.Method == "POST" && request.Target.Contains("fnId=azfn-slow", StringComparison.Ordinal))
            .Select(request => request.StatusCode)
            .ToList();
        statuses.ShouldContain(201);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_call_that_ends_within_three_seconds_is_answered_as_usual()
    {
        var eventId = await Api.SendEventAsync(AzureFunctionsFunctions.Greet, new GreetEvent(TestId, "Brno"), Cancellation);

        var run = await Api.WaitForRunEndAsync(eventId, Cancellation);

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<TextResult>().ShouldBe(new TextResult("HELLO, BRNO!"));
        var statuses = Fixture.App.Requests.Snapshot()
            .Where(request => request.Method == "POST" && request.Target.Contains("fnId=azfn-greet", StringComparison.Ordinal))
            .Select(request => request.StatusCode)
            .ToList();
        statuses.ShouldNotContain(201);
        statuses.ShouldAllBe(status => status == 200 || status == 206);
    }
}
