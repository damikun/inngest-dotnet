using Inngest.IntegrationTests.Functions;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.AwsLambda;

// Cloud mode behind the fake gateway, against a server that signs its requests: the signature covers the bytes of the
// body, which API Gateway may have turned into base64 on the way, and the app signs every response.
public sealed class SignedLambdaTests(SignedLambdaFixture fixture) : IntegrationTest<SignedLambdaFixture>(fixture), IClassFixture<SignedLambdaFixture>
{
    [Fact(Timeout = 60_000)]
    public async Task The_server_syncs_the_app_with_a_signed_request_through_the_handler()
    {
        var functions = await Api.GetAppFunctionsAsync(Fixture.AppId, Cancellation);
        var app = (await Api.GetAppsAsync(Cancellation)).Single(app => app.ExternalId == Fixture.AppId);

        functions.Select(function => function.Slug).ShouldBe([LambdaStepsFunction.Slug]);
        app.Error.ShouldBeNull();
        app.Framework.ShouldBe("aws-lambda");
        app.Url.ShouldBe(Fixture.App.ServerFacingUrl.AbsoluteUri);
        Fixture.Gateway.Calls.ShouldContain(call => call.Method == "PUT" && call.Signed && call.ResponseSigned);
    }

    [Fact(Timeout = 60_000)]
    public async Task Every_call_of_a_run_arrives_signed_and_is_answered_with_a_signature()
    {
        var calls = Fixture.Gateway.Calls.Count;

        var run = await RunToEndAsync(LambdaStepsFunction.EventName, new NumberEvent(TestId, 2));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<SequenceResult>().ShouldBe(new SequenceResult(3, 30, "3:30"));
        var served = Fixture.Gateway.Calls.Skip(calls).Where(call => call.Method == "POST").ToList();
        served.Count.ShouldBeGreaterThanOrEqualTo(4);
        served.ShouldAllBe(call => call.Signed && call.ResponseSigned);
    }

    [Fact(Timeout = 60_000)]
    public async Task The_signature_of_a_base64_body_is_checked_over_the_bytes_it_decodes_to()
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
            served.ShouldAllBe(call => call.IsBase64Encoded && call.Signed && call.ResponseSigned);
        }
        finally
        {
            Fixture.Gateway.Base64Bodies = false;
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task A_run_goes_through_rest_api_events_the_same_way()
    {
        Fixture.Gateway.Payload = GatewayPayload.RestApi;
        try
        {
            var calls = Fixture.Gateway.Calls.Count;

            var run = await RunToEndAsync(LambdaStepsFunction.EventName, new NumberEvent(TestId, 6));

            run.Status.ShouldBe(RunStatus.Completed);
            run.OutputAs<SequenceResult>().ShouldBe(new SequenceResult(7, 70, "7:70"));
            var served = Fixture.Gateway.Calls.Skip(calls).Where(call => call.Method == "POST").ToList();
            served.Count.ShouldBeGreaterThanOrEqualTo(4);
            served.ShouldAllBe(call => call.Payload == GatewayPayload.RestApi && call.Signed && call.ResponseSigned);
        }
        finally
        {
            Fixture.Gateway.Payload = GatewayPayload.HttpApi;
        }
    }
}
