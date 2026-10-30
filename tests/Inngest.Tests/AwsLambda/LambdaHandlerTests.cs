using Inngest.Tests.Serving;
using Inngest.Tests.Serving.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Tests.AwsLambda;

public sealed class LambdaHandlerTests
{
    private readonly LambdaHarness _lambda = new();

    [Fact]
    public void The_handler_needs_a_service_provider()
    {
        Should.Throw<ArgumentNullException>(() => new InngestLambdaHandler(null!));
    }

    [Fact]
    public void The_handler_fails_at_startup_when_AddInngest_was_not_called()
    {
        using var services = new ServiceCollection().BuildServiceProvider();

        var exception = Should.Throw<InvalidOperationException>(() => new InngestLambdaHandler(services));

        exception.Message.ShouldContain("AddInngest");
    }

    [Fact]
    public async Task A_missing_event_is_refused()
    {
        var handler = _lambda.Create(CommHandlerHarness.DevSettings);

        await Should.ThrowAsync<ArgumentNullException>(handler.HandleAsync((Amazon.Lambda.APIGatewayEvents.APIGatewayProxyRequest)null!, _lambda.Context));
        await Should.ThrowAsync<ArgumentNullException>(handler.HandleAsync((Amazon.Lambda.APIGatewayEvents.APIGatewayHttpApiV2ProxyRequest)null!, _lambda.Context));
    }

    [Fact]
    public async Task The_context_is_not_needed()
    {
        var handler = _lambda.Create(CommHandlerHarness.DevSettings);

        var rest = await handler.HandleAsync(LambdaEvents.Rest("GET", query: []), null!);
        var http = await handler.HandleAsync(LambdaEvents.HttpApi("GET", rawQueryString: string.Empty), null!);

        rest.StatusCode.ShouldBe(200);
        http.StatusCode.ShouldBe(200);
    }

    [Fact]
    public async Task Events_of_every_source_are_served_at_once()
    {
        var handler = _lambda.Create(CommHandlerHarness.DevSettings);

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 50).Select(async index => (index % 3) switch
            {
                0 => (await handler.HandleAsync(LambdaEvents.Rest(query: LambdaEvents.CallQuery, body: LambdaEvents.CallBody), _lambda.Context)).StatusCode,
                1 => (await handler.HandleAsync(LambdaEvents.HttpApi(body: LambdaEvents.CallBody), _lambda.Context)).StatusCode,
                _ => (await handler.HandleAsync(LambdaEvents.FunctionUrl(body: LambdaEvents.CallBody), _lambda.Context)).StatusCode,
            }));

        responses.ShouldAllBe(status => status == 200);
        _lambda.Serving.Engine.Calls.Count.ShouldBe(50);
    }

    [Fact]
    public async Task Streaming_has_no_effect_because_the_response_is_buffered_and_answers_once()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _lambda.Serving.Engine.Handler = async (_, _) =>
        {
            await release.Task;
            return FakeExecutionEngine.Result(200, "\"done\"");
        };
        var handler = _lambda.Create(CommHandlerHarness.DevSettings with { Streaming = true });

        var pending = handler.HandleAsync(LambdaEvents.HttpApi(body: LambdaEvents.CallBody), _lambda.Context);
        _lambda.Serving.Time.Advance(TimeSpan.FromMinutes(1));
        pending.IsCompleted.ShouldBeFalse();
        release.SetResult();
        var response = LambdaEvents.Wire(await pending);

        response.StatusCode.ShouldBe(200);
        response.Body.ShouldBe("\"done\"");
    }

    [Fact]
    public async Task An_app_registered_with_AddInngest_is_served_without_any_fake()
    {
        var services = new ServiceCollection();
        services.AddInngest(options =>
        {
            options.AppId = "shop";
            options.IsDev = true;
        });
        using var provider = services.BuildServiceProvider();
        var handler = new InngestLambdaHandler(provider);

        var response = LambdaEvents.Wire(await handler.HandleAsync(LambdaEvents.HttpApi("GET", rawQueryString: string.Empty), _lambda.Context));

        response.StatusCode.ShouldBe(200);
        response.Body.ShouldContain("\"function_count\":0");
        response.Body.ShouldContain("\"mode\":\"dev\"");
        response.Header("X-Inngest-Framework").ShouldBe("aws-lambda");
    }
}
