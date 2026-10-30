using System.Text;
using System.Text.Json.Nodes;
using Amazon.Lambda.APIGatewayEvents;
using Inngest.Internal;
using Inngest.Tests.Serving;
using Inngest.Tests.Serving.Fakes;

namespace Inngest.Tests.AwsLambda;

// Events of API Gateway HTTP APIs and Lambda function URLs, which use payload format 2.0.
public sealed class LambdaHttpApiTests
{
    private const string Operations = """[{"id":"e7d8a2f140845095749d60246ff1110c9d01d76a","op":"StepError"}]""";

    private readonly LambdaHarness _lambda = new();

    [Fact]
    public async Task GET_answers_the_introspection_body_as_the_aws_lambda_framework()
    {
        var handler = _lambda.Create(CommHandlerHarness.DevSettings);

        var response = LambdaEvents.Wire(await handler.HandleAsync(LambdaEvents.HttpApi("GET", rawQueryString: string.Empty), _lambda.Context));

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
    public async Task PUT_syncs_the_url_of_the_default_stage_without_the_deploy_id()
    {
        var handler = _lambda.Create(CommHandlerHarness.DevSettings);
        var request = LambdaEvents.HttpApi("PUT", rawQueryString: "region=eu&deployId=deploy-1", headers: [("X-Inngest-Server-Kind", "dev")]);

        var response = LambdaEvents.Wire(await handler.HandleAsync(request, _lambda.Context));

        response.StatusCode.ShouldBe(200);
        response.Header("X-Inngest-Sync-Kind").ShouldBe("out_of_band");
        var serveUrl = new Uri("https://a1b2c3d4e5.execute-api.eu-west-1.amazonaws.com/api/inngest?region=eu");
        _lambda.RegisteredUrl().ShouldBe(serveUrl);

        // Header names arrive lowercase, and the deploy id comes from the raw query string.
        _lambda.Serving.Api.Registrations.ShouldBe(
            [new FakeInngestApi.Registration(FakeFunctionRegistry.RegistrationJson(serveUrl, "aws-lambda"), "deploy-1", "dev")]);
    }

    [Fact]
    public async Task A_named_stage_is_already_in_the_raw_path_and_is_kept_once()
    {
        var url = await _lambda.SyncUrlAsync(LambdaEvents.HttpApi("PUT", rawPath: "/beta/api/inngest", rawQueryString: string.Empty, stage: "beta"));

        url.ShouldBe(new Uri("https://a1b2c3d4e5.execute-api.eu-west-1.amazonaws.com/beta/api/inngest"));
    }

    [Fact]
    public async Task A_custom_domain_shows_the_path_it_was_called_with()
    {
        var url = await _lambda.SyncUrlAsync(LambdaEvents.HttpApi("PUT", rawQueryString: string.Empty, host: "api.example.com", domainName: "api.example.com"));

        url.ShouldBe(new Uri("https://api.example.com/api/inngest"));
    }

    [Fact]
    public async Task A_function_url_registers_its_own_host_and_the_path_it_was_called_with()
    {
        var path = await _lambda.SyncUrlAsync(LambdaEvents.FunctionUrl("PUT", rawQueryString: string.Empty));
        var root = await _lambda.SyncUrlAsync(LambdaEvents.FunctionUrl("PUT", rawPath: "/", rawQueryString: string.Empty));

        path.ShouldBe(new Uri($"https://{LambdaEvents.FunctionUrlHost}/api/inngest"));
        root.ShouldBe(new Uri($"https://{LambdaEvents.FunctionUrlHost}/"));
    }

    [Fact]
    public async Task The_host_header_wins_and_the_domain_name_of_the_request_context_stands_in_for_it()
    {
        var header = await _lambda.SyncUrlAsync(LambdaEvents.HttpApi("PUT", rawQueryString: string.Empty, host: "api.example.com"));
        var context = await _lambda.SyncUrlAsync(LambdaEvents.HttpApi("PUT", rawQueryString: string.Empty, host: null));

        header.Host.ShouldBe("api.example.com");
        context.Host.ShouldBe(LambdaEvents.ExecuteApiHost);
    }

    [Theory]
    [InlineData("http", "http")]
    [InlineData("HTTP", "http")]
    [InlineData("https", "https")]
    [InlineData("https,http", "https")]
    [InlineData("http, https", "http")]
    [InlineData("ftp", "https")]
    [InlineData("", "https")]
    [InlineData(null, "https")]
    public async Task The_scheme_is_the_first_one_the_forwarded_proto_header_lists_and_otherwise_https(string? forwardedProto, string expected)
    {
        var url = await _lambda.SyncUrlAsync(LambdaEvents.HttpApi("PUT", rawQueryString: string.Empty, forwardedProto: forwardedProto));

        url.Scheme.ShouldBe(expected);
    }

    [Fact]
    public async Task ServeOrigin_and_ServePath_replace_what_the_event_shows()
    {
        var settings = CommHandlerHarness.DevSettings with { ServeOrigin = new Uri("https://api.example.com"), ServePath = "/v1/api/inngest" };

        var url = await _lambda.SyncUrlAsync(LambdaEvents.HttpApi("PUT", rawQueryString: "region=eu"), settings);

        url.ShouldBe(new Uri("https://api.example.com/v1/api/inngest?region=eu"));
    }

    [Fact]
    public async Task An_event_that_names_no_host_cannot_be_synced_without_ServeOrigin()
    {
        var handler = _lambda.Create(CommHandlerHarness.DevSettings);
        var request = LambdaEvents.HttpApi("PUT", rawQueryString: string.Empty, host: null, domainName: string.Empty);

        var response = LambdaEvents.Wire(await handler.HandleAsync(request, _lambda.Context));

        response.StatusCode.ShouldBe(500);
        response.Body.ShouldContain("ServeOrigin");
        _lambda.Serving.Api.Registrations.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_raw_query_string_decides_what_a_parameter_is_because_the_parameter_dictionary_is_decoded_twice()
    {
        // The shape of the AWS sample for encoded query strings: %2B arrives in queryStringParameters as a space.
        var handler = _lambda.Create(CommHandlerHarness.DevSettings);
        var request = LambdaEvents.HttpApi(
            rawQueryString: "fnId=shop-send-email&stepId=2019-03-12T16%3A06%3A06%2B00%3A00",
            queryStringParameters: new JsonObject { ["fnId"] = "shop-send-email", ["stepId"] = "2019-03-12T16:06:06 00:00" },
            body: LambdaEvents.CallBody);

        await handler.HandleAsync(request, _lambda.Context);

        _lambda.Serving.Engine.Calls.ShouldHaveSingleItem().StepId.ShouldBe("2019-03-12T16:06:06+00:00");
    }

    [Fact]
    public async Task The_query_parameters_stand_in_for_a_missing_raw_query_string()
    {
        var handler = _lambda.Create(CommHandlerHarness.DevSettings);
        var request = LambdaEvents.HttpApi(
            rawQueryString: null!,
            queryStringParameters: new JsonObject { ["fnId"] = "shop-send-email", ["stepId"] = "a b,c" },
            body: LambdaEvents.CallBody);

        await handler.HandleAsync(request, _lambda.Context);

        _lambda.Serving.Engine.Calls.ShouldHaveSingleItem().StepId.ShouldBe("a b,c");
    }

    [Fact]
    public async Task POST_hands_the_body_the_step_and_the_scope_to_the_engine_and_returns_its_answer()
    {
        _lambda.Serving.Engine.Handler = (_, _) => Task.FromResult(FakeExecutionEngine.Result(206, Operations, noRetry: false, retryAfter: "30"));
        var handler = _lambda.Create(CommHandlerHarness.DevSettings);
        var request = LambdaEvents.HttpApi(
            headers: [("X-Request-Id", "req-1"), ("X-Inngest-Job-Id", "job-1"), ("X-Inngest-Generation-Id", "3")],
            cookies: ["session=abc", "theme=dark"],
            body: LambdaEvents.CallBody);

        var response = LambdaEvents.Wire(await handler.HandleAsync(request, _lambda.Context));

        var call = _lambda.Serving.Engine.Calls.ShouldHaveSingleItem();
        call.Function.ShouldBeSameAs(_lambda.Serving.Registry.Definitions[0]);
        call.BodyText.ShouldBe(LambdaEvents.CallBody);
        call.StepId.ShouldBe("step");
        call.RequestId.ShouldBe("req-1");
        call.JobId.ShouldBe("job-1");
        call.GenerationId.ShouldBe(3);
        response.StatusCode.ShouldBe(206);
        response.Body.ShouldBe(Operations);
        response.Header("X-Inngest-No-Retry").ShouldBe("false");
        response.Header("Retry-After").ShouldBe("30");

        // The headers are the only thing besides status and body that the response carries.
        response.Json.EnumerateObject().Select(property => property.Name).Order().ShouldBe(["body", "headers", "isBase64Encoded", "statusCode"]);
    }

    [Fact]
    public async Task A_function_url_runs_calls_like_an_http_api()
    {
        var handler = _lambda.Create(CommHandlerHarness.DevSettings);

        var response = LambdaEvents.Wire(await handler.HandleAsync(LambdaEvents.FunctionUrl(body: LambdaEvents.CallBody), _lambda.Context));

        response.StatusCode.ShouldBe(200);
        response.Body.ShouldBe("\"done\"");
        _lambda.Serving.Engine.Calls.ShouldHaveSingleItem().BodyText.ShouldBe(LambdaEvents.CallBody);
    }

    [Fact]
    public async Task A_base64_body_is_decoded_before_the_engine_and_the_signature_see_it()
    {
        var handler = _lambda.Create(CommHandlerHarness.CloudSettings);
        var request = LambdaEvents.HttpApi(
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

        await handler.HandleAsync(LambdaEvents.HttpApi(body: Body), _lambda.Context);

        _lambda.Serving.Engine.Calls.ShouldHaveSingleItem().Body.ShouldBe(Encoding.UTF8.GetBytes(Body));
    }

    [Fact]
    public async Task An_empty_answer_is_an_empty_body()
    {
        var handler = _lambda.Create(CommHandlerHarness.DevSettings);

        var response = LambdaEvents.Wire(await handler.HandleAsync(LambdaEvents.HttpApi(rawQueryString: "probe=trust", body: "{}"), _lambda.Context));

        response.StatusCode.ShouldBe(200);
        response.Body.ShouldBeEmpty();
    }

    [Fact]
    public async Task Only_GET_PUT_and_POST_are_served()
    {
        var handler = _lambda.Create(CommHandlerHarness.DevSettings);

        var response = LambdaEvents.Wire(await handler.HandleAsync(LambdaEvents.HttpApi("OPTIONS"), _lambda.Context));

        response.StatusCode.ShouldBe(405);
        _lambda.Serving.Engine.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_event_without_an_http_method_is_not_an_http_api_event()
    {
        var handler = _lambda.Create(CommHandlerHarness.DevSettings);

        var exception = await Should.ThrowAsync<ArgumentException>(handler.HandleAsync(new APIGatewayHttpApiV2ProxyRequest(), _lambda.Context));

        exception.Message.ShouldContain("requestContext.http.method");
        exception.Message.ShouldContain("payload format 1.0");
    }
}
