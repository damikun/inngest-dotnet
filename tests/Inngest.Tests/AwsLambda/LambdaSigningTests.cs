using Inngest.Tests.Serving;
using Inngest.Tests.Serving.Fakes;

namespace Inngest.Tests.AwsLambda;

// Cloud mode: the signature is checked over the bytes of the body as Inngest sent them, and the response is signed over
// the bytes API Gateway sends on, however the event and the response travel through Lambda's JSON.
public sealed class LambdaSigningTests
{
    private const string Output = "\"Zoë 😀 日本語 \\\" \\\\ \\u0007\"";

    private readonly LambdaHarness _lambda = new();

    [Fact]
    public async Task A_signed_call_through_a_rest_api_is_answered_with_a_signature_of_the_response_body()
    {
        _lambda.Serving.Engine.Handler = (_, _) => Task.FromResult(FakeExecutionEngine.Result(200, Output));
        var handler = _lambda.Create(CommHandlerHarness.CloudSettings);
        var request = LambdaEvents.Rest(query: LambdaEvents.CallQuery, headers: [("X-Inngest-Signature", _lambda.Sign(LambdaEvents.CallBody))], body: LambdaEvents.CallBody);

        var response = LambdaEvents.Wire(await handler.HandleAsync(request, _lambda.Context));

        response.StatusCode.ShouldBe(200);
        response.Body.ShouldBe(Output);
        IsSignatureOfBody(response).ShouldBeTrue();
    }

    [Fact]
    public async Task A_signed_call_through_an_http_api_is_answered_with_a_signature_of_the_response_body()
    {
        _lambda.Serving.Engine.Handler = (_, _) => Task.FromResult(FakeExecutionEngine.Result(200, Output));
        var handler = _lambda.Create(CommHandlerHarness.CloudSettings);
        var request = LambdaEvents.HttpApi(headers: [("X-Inngest-Signature", _lambda.Sign(LambdaEvents.CallBody))], body: LambdaEvents.CallBody);

        var response = LambdaEvents.Wire(await handler.HandleAsync(request, _lambda.Context));

        response.StatusCode.ShouldBe(200);
        response.Body.ShouldBe(Output);
        IsSignatureOfBody(response).ShouldBeTrue();
    }

    [Fact]
    public async Task A_signed_call_through_a_function_url_is_answered_with_a_signature_of_the_response_body()
    {
        _lambda.Serving.Engine.Handler = (_, _) => Task.FromResult(FakeExecutionEngine.Result(200, Output));
        var handler = _lambda.Create(CommHandlerHarness.CloudSettings);
        var request = LambdaEvents.FunctionUrl(headers: [("X-Inngest-Signature", _lambda.Sign(LambdaEvents.CallBody))], body: LambdaEvents.CallBody);

        var response = LambdaEvents.Wire(await handler.HandleAsync(request, _lambda.Context));

        response.StatusCode.ShouldBe(200);
        IsSignatureOfBody(response).ShouldBeTrue();
    }

    [Fact]
    public async Task The_signature_of_a_base64_body_is_the_signature_of_the_bytes_it_decodes_to()
    {
        var handler = _lambda.Create(CommHandlerHarness.CloudSettings);
        var request = LambdaEvents.Rest(
            query: LambdaEvents.CallQuery,
            headers: [("X-Inngest-Signature", _lambda.Sign(LambdaEvents.CallBody))],
            body: LambdaEvents.Base64(LambdaEvents.CallBody),
            isBase64Encoded: true);

        var response = LambdaEvents.Wire(await handler.HandleAsync(request, _lambda.Context));

        response.StatusCode.ShouldBe(200);
        IsSignatureOfBody(response).ShouldBeTrue();
    }

    [Fact]
    public async Task A_signature_made_over_the_base64_text_does_not_pass()
    {
        var handler = _lambda.Create(CommHandlerHarness.CloudSettings);
        var encoded = LambdaEvents.Base64(LambdaEvents.CallBody);
        var request = LambdaEvents.HttpApi(headers: [("X-Inngest-Signature", _lambda.Sign(encoded))], body: encoded, isBase64Encoded: true);

        var response = LambdaEvents.Wire(await handler.HandleAsync(request, _lambda.Context));

        response.StatusCode.ShouldBe(401);
        _lambda.Serving.Engine.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_call_without_a_signature_is_turned_away_and_learns_nothing_about_the_sdk()
    {
        var handler = _lambda.Create(CommHandlerHarness.CloudSettings);

        var response = LambdaEvents.Wire(await handler.HandleAsync(LambdaEvents.HttpApi(body: LambdaEvents.CallBody), _lambda.Context));

        response.StatusCode.ShouldBe(401);
        response.Body.ShouldBe("""{"message":"Unauthorized"}""");
        response.Header("X-Inngest-SDK").ShouldBeNull();
        response.Header("X-Inngest-Signature").ShouldBeNull();
        _lambda.Serving.Engine.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_signature_of_another_body_is_turned_away()
    {
        var handler = _lambda.Create(CommHandlerHarness.CloudSettings);
        var request = LambdaEvents.Rest(query: LambdaEvents.CallQuery, headers: [("X-Inngest-Signature", _lambda.Sign("{}"))], body: LambdaEvents.CallBody);

        var response = LambdaEvents.Wire(await handler.HandleAsync(request, _lambda.Context));

        response.StatusCode.ShouldBe(401);
        _lambda.Serving.Engine.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_signed_introspection_answers_the_authenticated_body()
    {
        var handler = _lambda.Create(CommHandlerHarness.CloudSettings);
        var request = LambdaEvents.HttpApi("GET", rawQueryString: string.Empty, headers: [("X-Inngest-Signature", _lambda.Sign(string.Empty))]);

        var response = LambdaEvents.Wire(await handler.HandleAsync(request, _lambda.Context));

        response.StatusCode.ShouldBe(200);
        response.Body.ShouldContain("\"authentication_succeeded\":true");
        IsSignatureOfBody(response).ShouldBeTrue();
    }

    // Checks the response the way Inngest would: the signature in the headers against the bytes of the body.
    private bool IsSignatureOfBody(WireResponse response) =>
        _lambda.Serving.Signer
            .Verify(response.Header("X-Inngest-Signature"), response.BodyBytes, CommHandlerHarness.PrimaryKey, fallbackKey: null)
            .IsValid;
}
