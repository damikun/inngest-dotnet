using System.Text;
using Amazon.Lambda.APIGatewayEvents;
using Inngest.Configuration;
using Inngest.Internal;
using Inngest.Tests.Serving;
using Inngest.Tests.Serving.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Tests.AwsLambda;

// Events of API Gateway REST APIs, which use payload format 1.0.
public sealed class LambdaRestApiTests
{
    private const string Operations = """[{"id":"e7d8a2f140845095749d60246ff1110c9d01d76a","op":"StepError"}]""";

    private readonly LambdaHarness _lambda = new();

    [Fact]
    public async Task GET_answers_the_introspection_body_as_the_aws_lambda_framework()
    {
        var handler = _lambda.Create(CommHandlerHarness.DevSettings);

        var response = LambdaEvents.Wire(await handler.HandleAsync(LambdaEvents.Rest("GET", query: []), _lambda.Context));

        response.StatusCode.ShouldBe(200);
        response.Body.ShouldBe(
            """{"authentication_succeeded":null,"capabilities":{"trust_probe":"v1"},"function_count":2,"has_event_key":false,"has_signing_key":false,"has_signing_key_fallback":false,"mode":"dev","schema_version":"2024-05-24"}""");
        response.IsBase64Encoded.ShouldBeFalse();
        response.Header("Content-Type").ShouldBe("application/json");
        response.Header("X-Inngest-SDK").ShouldBe(SdkInfo.Header);
        response.Header("X-Inngest-Framework").ShouldBe("aws-lambda");
        response.Header("X-Inngest-Req-Version").ShouldBe("2");
    }

    [Fact]
    public async Task PUT_syncs_with_the_stage_of_an_execute_api_domain_in_the_url()
    {
        var handler = _lambda.Create(CommHandlerHarness.DevSettings);
        var request = LambdaEvents.Rest("PUT", query: [("region", "eu"), ("deployId", "deploy-1")], headers: [("X-Inngest-Server-Kind", "dev")]);

        var response = LambdaEvents.Wire(await handler.HandleAsync(request, _lambda.Context));

        response.StatusCode.ShouldBe(200);
        response.Body.ShouldBe("""{"message":"Successfully registered","modified":true}""");
        response.Header("X-Inngest-Sync-Kind").ShouldBe("out_of_band");
        var serveUrl = new Uri("https://a1b2c3d4e5.execute-api.eu-west-1.amazonaws.com/prod/api/inngest?region=eu");
        _lambda.RegisteredUrl().ShouldBe(serveUrl);

        // The deploy id and the server kind come from the query and the headers, whatever their case.
        _lambda.Serving.Api.Registrations.ShouldBe(
            [new FakeInngestApi.Registration(FakeFunctionRegistry.RegistrationJson(serveUrl, "aws-lambda"), "deploy-1", "dev")]);
    }

    [Fact]
    public async Task An_in_band_sync_reports_the_url_in_its_body_whatever_the_event_shows()
    {
        var handler = _lambda.Create(CommHandlerHarness.DevSettings);
        var request = LambdaEvents.Rest(
            "PUT",
            contextPath: null,
            host: null,
            headers: [("X-Inngest-Sync-Kind", "in_band")],
            body: """{"url":"https://shop.example.com/prod/api/inngest"}""");

        var response = LambdaEvents.Wire(await handler.HandleAsync(request, _lambda.Context));

        response.StatusCode.ShouldBe(200);
        response.Header("X-Inngest-Sync-Kind").ShouldBe("in_band");
        _lambda.Serving.Registry.Registrations.ShouldHaveSingleItem().ServeUrl.ShouldBe(new Uri("https://shop.example.com/prod/api/inngest"));
        _lambda.Serving.Api.Registrations.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_custom_domain_without_a_base_path_has_no_stage_in_the_url()
    {
        var url = await SyncUrlAsync(LambdaEvents.Rest("PUT", contextPath: "/api/inngest", host: "api.example.com"));

        url.ShouldBe(new Uri("https://api.example.com/api/inngest"));
    }

    [Fact]
    public async Task A_base_path_mapping_of_a_custom_domain_is_in_path_and_in_the_request_context_path_alike()
    {
        // As logged from a REST API behind a custom domain with the base path mapping "test" and the stage Production
        // (aws-lambda-dotnet issue 127): both paths have the mapping, and neither has the stage.
        var url = await SyncUrlAsync(LambdaEvents.Rest("PUT", path: "/test/foo/bar", contextPath: "/test/foo/bar", stage: "Production", host: "api.emg-services.net"));

        url.ShouldBe(new Uri("https://api.emg-services.net/test/foo/bar"));
    }

    [Fact]
    public async Task The_same_api_on_its_execute_api_domain_has_the_stage_in_the_request_context_path_only()
    {
        // The other request of that issue: path leaves the stage out and requestContext.path has it.
        var url = await SyncUrlAsync(
            LambdaEvents.Rest("PUT", path: "/foo/bar", contextPath: "/Production/foo/bar", stage: "Production", host: "6bgh10txr3.execute-api.eu-west-1.amazonaws.com"));

        url.ShouldBe(new Uri("https://6bgh10txr3.execute-api.eu-west-1.amazonaws.com/Production/foo/bar"));
    }

    [Fact]
    public async Task Serving_at_the_root_of_a_stage_names_the_stage()
    {
        var url = await SyncUrlAsync(LambdaEvents.Rest("PUT", path: "/", contextPath: "/prod"));

        url.ShouldBe(new Uri("https://a1b2c3d4e5.execute-api.eu-west-1.amazonaws.com/prod"));
    }

    [Fact]
    public async Task An_event_without_a_request_context_path_uses_path()
    {
        var url = await SyncUrlAsync(LambdaEvents.Rest("PUT", contextPath: null));

        url.ShouldBe(new Uri("https://a1b2c3d4e5.execute-api.eu-west-1.amazonaws.com/api/inngest"));
    }

    [Fact]
    public async Task A_request_context_path_that_does_not_extend_path_leaves_path_in_charge()
    {
        // HTTP APIs with payload format 1.0 put the stage in path and not in requestContext.path.
        var url = await SyncUrlAsync(LambdaEvents.Rest("PUT", path: "/beta/api/inngest", contextPath: "/api/inngest", stage: "beta"));

        url.ShouldBe(new Uri("https://a1b2c3d4e5.execute-api.eu-west-1.amazonaws.com/beta/api/inngest"));
    }

    [Fact]
    public async Task The_scheme_is_the_one_the_caller_used_and_https_when_the_event_does_not_say()
    {
        var local = await SyncUrlAsync(LambdaEvents.Rest("PUT", host: "localhost:3001", forwardedProto: "http"));
        var unknown = await SyncUrlAsync(LambdaEvents.Rest("PUT", forwardedProto: null));

        local.ShouldBe(new Uri("http://localhost:3001/prod/api/inngest"));
        unknown.Scheme.ShouldBe("https");
    }

    [Fact]
    public async Task ServeOrigin_and_ServePath_replace_what_the_event_shows()
    {
        var settings = CommHandlerHarness.DevSettings with { ServeOrigin = new Uri("https://api.example.com"), ServePath = "/v1/api/inngest" };

        var url = await SyncUrlAsync(LambdaEvents.Rest("PUT", query: [("region", "eu")]), settings);

        url.ShouldBe(new Uri("https://api.example.com/v1/api/inngest?region=eu"));
    }

    [Fact]
    public async Task An_event_that_names_no_host_cannot_be_synced_without_ServeOrigin()
    {
        var handler = _lambda.Create(CommHandlerHarness.DevSettings);

        var response = LambdaEvents.Wire(await handler.HandleAsync(LambdaEvents.Rest("PUT", host: null), _lambda.Context));

        response.StatusCode.ShouldBe(500);
        response.Body.ShouldContain("ServeOrigin");
        _lambda.Serving.Api.Registrations.ShouldBeEmpty();
    }

    [Fact]
    public async Task POST_hands_the_body_the_step_and_the_scope_to_the_engine_and_returns_its_answer()
    {
        _lambda.Serving.Engine.Handler = (_, _) => Task.FromResult(FakeExecutionEngine.Result(206, Operations, noRetry: false, retryAfter: "30"));
        var handler = _lambda.Create(CommHandlerHarness.DevSettings);

        var response = LambdaEvents.Wire(await handler.HandleAsync(LambdaEvents.Rest(query: LambdaEvents.CallQuery, body: LambdaEvents.CallBody), _lambda.Context));

        var call = _lambda.Serving.Engine.Calls.ShouldHaveSingleItem();
        call.Function.ShouldBeSameAs(_lambda.Serving.Registry.Definitions[0]);
        call.BodyText.ShouldBe(LambdaEvents.CallBody);
        call.StepId.ShouldBe("step");
        response.StatusCode.ShouldBe(206);
        response.Body.ShouldBe(Operations);
        response.Header("X-Inngest-No-Retry").ShouldBe("false");
        response.Header("Retry-After").ShouldBe("30");

        // The headers are the only thing besides status and body that the response carries.
        response.Json.EnumerateObject().Select(property => property.Name).Order().ShouldBe(["body", "headers", "isBase64Encoded", "statusCode"]);
    }

    [Fact]
    public async Task Query_values_arrive_decoded_whichever_form_the_event_uses()
    {
        var handler = _lambda.Create(CommHandlerHarness.DevSettings);
        var step = "a b+c%d/é&=;";
        (string, string)[] query = [("fnId", "shop-send-email"), ("stepId", step)];

        await handler.HandleAsync(LambdaEvents.Rest(query: query, body: LambdaEvents.CallBody), _lambda.Context);
        await handler.HandleAsync(LambdaEvents.Rest(query: query, body: LambdaEvents.CallBody, singleValueOnly: true), _lambda.Context);

        _lambda.Serving.Engine.Calls.Select(call => call.StepId).ShouldBe([step, step]);
    }

    [Fact]
    public async Task The_values_of_a_repeated_query_parameter_are_joined_with_commas_as_in_ASP_NET_Core()
    {
        var handler = _lambda.Create(CommHandlerHarness.DevSettings);

        await handler.HandleAsync(
            LambdaEvents.Rest(query: [("fnId", "shop-send-email"), ("stepId", "one"), ("stepId", "two")], body: LambdaEvents.CallBody),
            _lambda.Context);

        _lambda.Serving.Engine.Calls.ShouldHaveSingleItem().StepId.ShouldBe("one,two");
    }

    [Theory]
    [InlineData("x-inngest-signature", false)]
    [InlineData("X-INNGEST-SIGNATURE", false)]
    [InlineData("X-Inngest-Signature", false)]
    [InlineData("x-inngest-signature", true)]
    public async Task Headers_are_found_whatever_their_case_in_either_form(string name, bool singleValueOnly)
    {
        var handler = _lambda.Create(CommHandlerHarness.CloudSettings);
        var request = LambdaEvents.Rest(
            query: LambdaEvents.CallQuery,
            headers: [(name, _lambda.Sign(LambdaEvents.CallBody))],
            singleValueOnly: singleValueOnly,
            body: LambdaEvents.CallBody);

        var response = LambdaEvents.Wire(await handler.HandleAsync(request, _lambda.Context));

        response.StatusCode.ShouldBe(200);
        _lambda.Serving.Engine.Calls.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task The_trace_context_of_the_caller_is_passed_back()
    {
        var handler = _lambda.Create(CommHandlerHarness.DevSettings);
        var request = LambdaEvents.Rest(
            query: LambdaEvents.CallQuery,
            headers: [("Traceparent", "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01")],
            body: LambdaEvents.CallBody);

        var response = LambdaEvents.Wire(await handler.HandleAsync(request, _lambda.Context));

        response.Header("traceparent").ShouldBe("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");
    }

    [Fact]
    public async Task A_base64_body_is_decoded_before_the_engine_and_the_signature_see_it()
    {
        var handler = _lambda.Create(CommHandlerHarness.CloudSettings);
        var request = LambdaEvents.Rest(
            query: LambdaEvents.CallQuery,
            headers: [("X-Inngest-Signature", _lambda.Sign(LambdaEvents.CallBody))],
            body: LambdaEvents.Base64(LambdaEvents.CallBody),
            isBase64Encoded: true);

        var response = LambdaEvents.Wire(await handler.HandleAsync(request, _lambda.Context));

        response.StatusCode.ShouldBe(200);
        _lambda.Serving.Engine.Calls.ShouldHaveSingleItem().BodyText.ShouldBe(LambdaEvents.CallBody);
    }

    [Fact]
    public async Task A_body_with_text_outside_ASCII_reaches_the_engine_as_the_UTF_8_of_the_text()
    {
        const string Body = """{"event":{"name":"user/signed.up","data":{"name":"Zoë 😀 日本語"}}}""";
        var handler = _lambda.Create(CommHandlerHarness.DevSettings);

        await handler.HandleAsync(LambdaEvents.Rest(query: LambdaEvents.CallQuery, body: Body), _lambda.Context);

        _lambda.Serving.Engine.Calls.ShouldHaveSingleItem().Body.ShouldBe(Encoding.UTF8.GetBytes(Body));
    }

    [Fact]
    public async Task A_call_without_a_body_is_turned_away()
    {
        var handler = _lambda.Create(CommHandlerHarness.DevSettings);

        var response = LambdaEvents.Wire(await handler.HandleAsync(LambdaEvents.Rest(query: LambdaEvents.CallQuery), _lambda.Context));

        response.StatusCode.ShouldBe(401);
        _lambda.Serving.Engine.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_empty_answer_is_an_empty_body()
    {
        var handler = _lambda.Create(CommHandlerHarness.DevSettings);

        var response = LambdaEvents.Wire(await handler.HandleAsync(LambdaEvents.Rest(query: [("probe", "trust")], body: "{}"), _lambda.Context));

        response.StatusCode.ShouldBe(200);
        response.Body.ShouldBeEmpty();
        response.IsBase64Encoded.ShouldBeFalse();
    }

    [Fact]
    public async Task Only_GET_PUT_and_POST_are_served()
    {
        var handler = _lambda.Create(CommHandlerHarness.DevSettings);

        var response = LambdaEvents.Wire(await handler.HandleAsync(LambdaEvents.Rest("DELETE"), _lambda.Context));

        response.StatusCode.ShouldBe(405);
        _lambda.Serving.Engine.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Every_invocation_runs_its_functions_in_a_scope_of_its_own_that_is_disposed_after_the_call()
    {
        var scoped = new List<ScopedDependency>();
        _lambda.Serving.Engine.Handler = (request, _) =>
        {
            scoped.Add(request.Services.GetRequiredService<ScopedDependency>());
            return Task.FromResult(FakeExecutionEngine.Result(200, "null"));
        };
        var handler = _lambda.Create(CommHandlerHarness.DevSettings);

        await handler.HandleAsync(LambdaEvents.Rest(query: LambdaEvents.CallQuery, body: LambdaEvents.CallBody), _lambda.Context);
        await handler.HandleAsync(LambdaEvents.Rest(query: LambdaEvents.CallQuery, body: LambdaEvents.CallBody), _lambda.Context);

        scoped.Count.ShouldBe(2);
        scoped[0].ShouldNotBeSameAs(scoped[1]);
        scoped.ShouldAllBe(dependency => dependency.IsDisposed);
    }

    [Fact]
    public async Task The_cancellation_token_of_functions_is_never_cancelled_whatever_time_the_invocation_has_left()
    {
        _lambda.Context.RemainingTime = TimeSpan.Zero;
        var handler = _lambda.Create(CommHandlerHarness.DevSettings);

        await handler.HandleAsync(LambdaEvents.Rest(query: LambdaEvents.CallQuery, body: LambdaEvents.CallBody), _lambda.Context);

        var call = _lambda.Serving.Engine.Calls.ShouldHaveSingleItem();
        call.CancellationToken.CanBeCanceled.ShouldBeFalse();
    }

    [Fact]
    public async Task An_event_without_an_http_method_is_not_a_rest_api_event()
    {
        var handler = _lambda.Create(CommHandlerHarness.DevSettings);

        var exception = await Should.ThrowAsync<ArgumentException>(handler.HandleAsync(new APIGatewayProxyRequest(), _lambda.Context));

        exception.Message.ShouldContain("httpMethod");
        exception.Message.ShouldContain("payload format 2.0");
    }

    private Task<Uri> SyncUrlAsync(APIGatewayProxyRequest request, InngestSettings? settings = null) => _lambda.SyncUrlAsync(request, settings);
}
