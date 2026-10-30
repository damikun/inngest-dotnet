using System.Net;
using Inngest.IntegrationTests.Functions;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.AwsLambda;

// The Inngest endpoint served through InngestLambdaHandler, with the dev server syncing the app and running a function
// whose calls all pass through events and responses of API Gateway's payload formats.
public sealed class LambdaTests(LambdaFixture fixture) : IntegrationTest<LambdaFixture>(fixture), IClassFixture<LambdaFixture>
{
    private static readonly HttpClient Http = new();

    [Fact(Timeout = 60_000)]
    public async Task The_dev_server_syncs_the_app_through_the_handler_and_the_app_reports_the_aws_lambda_framework()
    {
        var app = (await Api.GetAppsAsync(Cancellation)).Single(app => app.ExternalId == Fixture.AppId);
        var functions = await Api.GetAppFunctionsAsync(Fixture.AppId, Cancellation);

        app.Error.ShouldBeNull();
        app.Framework.ShouldBe("aws-lambda");
        app.SdkLanguage.ShouldBe("dotnet");
        app.Url.ShouldBe(Fixture.App.ServerFacingUrl.AbsoluteUri);
        app.FunctionCount.ShouldBe(1);
        var function = functions.ShouldHaveSingleItem();
        function.Slug.ShouldBe(LambdaStepsFunction.Slug);
        function.StepUrl.ShouldBe($"{Fixture.App.ServerFacingUrl}?fnId={LambdaStepsFunction.Slug}&stepId=step");
        Fixture.Gateway.Calls.ShouldContain(call => call.Method == "PUT" && call.Payload == GatewayPayload.HttpApi);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_function_with_steps_and_a_sleep_runs_over_http_api_events()
    {
        var calls = Fixture.Gateway.Calls.Count;

        var run = await RunToEndAsync(LambdaStepsFunction.EventName, new NumberEvent(TestId, 2));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<SequenceResult>().ShouldBe(new SequenceResult(3, 30, "3:30"));
        Observed.Names.ShouldBe(["first", "second"]);

        // One call per step, one after the sleep and one that returns the output, all as HTTP API events.
        var served = Fixture.Gateway.Calls.Skip(calls).Where(call => call.Method == "POST").ToList();
        served.Count.ShouldBeGreaterThanOrEqualTo(4);
        served.ShouldAllBe(call => call.Payload == GatewayPayload.HttpApi && !call.IsBase64Encoded);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_function_runs_over_events_with_base64_bodies()
    {
        Fixture.Gateway.Base64Bodies = true;
        try
        {
            var calls = Fixture.Gateway.Calls.Count;

            var run = await RunToEndAsync(LambdaStepsFunction.EventName, new NumberEvent(TestId, 4));

            run.Status.ShouldBe(RunStatus.Completed);
            run.OutputAs<SequenceResult>().ShouldBe(new SequenceResult(5, 50, "5:50"));
            var served = Fixture.Gateway.Calls.Skip(calls).Where(call => call.Method == "POST").ToList();
            served.Count.ShouldBeGreaterThanOrEqualTo(4);
            served.ShouldAllBe(call => call.IsBase64Encoded);
        }
        finally
        {
            Fixture.Gateway.Base64Bodies = false;
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task A_rest_api_stage_is_part_of_the_synced_url_and_a_function_runs_over_rest_api_events()
    {
        Fixture.Gateway.Payload = GatewayPayload.RestApi;
        try
        {
            // The sync Inngest started was an HTTP API event; this one is a REST API event for the same address, as
            // the dev server would send it, and the app must report the same URL: the stage is in requestContext.path
            // and not in path.
            using var sync = new HttpRequestMessage(HttpMethod.Put, Fixture.App.LocalUrl);
            sync.Headers.Host = $"host.docker.internal:{Fixture.App.Port}";
            sync.Headers.Add("X-Inngest-Server-Kind", "dev");
            using var syncResponse = await Http.SendAsync(sync, Cancellation);
            syncResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

            var app = (await Api.GetAppsAsync(Cancellation)).Where(app => app.ExternalId == Fixture.AppId).ShouldHaveSingleItem();
            app.Url.ShouldBe(Fixture.App.ServerFacingUrl.AbsoluteUri);
            app.Framework.ShouldBe("aws-lambda");
            var function = (await Api.GetAppFunctionsAsync(Fixture.AppId, Cancellation)).ShouldHaveSingleItem();
            function.StepUrl.ShouldBe($"{Fixture.App.ServerFacingUrl}?fnId={LambdaStepsFunction.Slug}&stepId=step");

            var calls = Fixture.Gateway.Calls.Count;
            var run = await RunToEndAsync(LambdaStepsFunction.EventName, new NumberEvent(TestId, 6));

            run.Status.ShouldBe(RunStatus.Completed);
            run.OutputAs<SequenceResult>().ShouldBe(new SequenceResult(7, 70, "7:70"));
            var served = Fixture.Gateway.Calls.Skip(calls).Where(call => call.Method == "POST").ToList();
            served.Count.ShouldBeGreaterThanOrEqualTo(4);
            served.ShouldAllBe(call => call.Payload == GatewayPayload.RestApi);
        }
        finally
        {
            Fixture.Gateway.Payload = GatewayPayload.HttpApi;
        }
    }
}
