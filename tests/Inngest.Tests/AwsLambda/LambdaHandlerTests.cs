using System.Text;
using Inngest.Execution;
using Inngest.Tests.Serving;
using Inngest.Tests.Serving.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
        const int Calls = 60;
        var engine = new GatedEngine(Calls);
        var handler = _lambda.Create(CommHandlerHarness.DevSettings, engine);

        // The calls run on pool threads and all of them are inside the engine, each holding the buffer of its own
        // body, before any is let go. A buffer that went back to the pool early, or state shared between calls, would
        // show as a body that is not the one the call sent.
        var running = Enumerable.Range(0, Calls).Select(index => Task.Run(() => ServeAsync(handler, index))).ToArray();
        await engine.AllEntered.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        engine.Running.ShouldBe(Calls);
        engine.Release();
        var answers = await Task.WhenAll(running);

        for (var index = 0; index < Calls; index++)
        {
            answers[index].StatusCode.ShouldBe(200);
            answers[index].Body.ShouldBe(BodyOf(index));
        }
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
    public async Task Streaming_logs_one_warning_when_the_handler_is_built_and_nothing_afterwards()
    {
        var (handler, logs) = _lambda.CreateLogged(CommHandlerHarness.DevSettings with { Streaming = true });
        var built = logs.GetSnapshot().Where(record => record.Id.Id == 380).ToList();

        var warning = built.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldContain("buffered");
        warning.Category.ShouldBe("Inngest.InngestLambdaHandler");

        // A call is answered as usual, and says nothing more about it.
        var response = await handler.HandleAsync(LambdaEvents.HttpApi(body: LambdaEvents.CallBody), _lambda.Context);
        response.StatusCode.ShouldBe(200);
        logs.GetSnapshot().Count(record => record.Id.Id == 380).ShouldBe(1);
    }

    [Fact]
    public void Without_streaming_the_handler_says_nothing_when_it_is_built()
    {
        var (_, logs) = _lambda.CreateLogged(CommHandlerHarness.DevSettings);

        logs.GetSnapshot().ShouldBeEmpty();
    }

    [Fact]
    public void Streaming_is_no_reason_to_fail_in_an_app_that_does_not_log()
    {
        Should.NotThrow(() => _lambda.Create(CommHandlerHarness.DevSettings with { Streaming = true }));
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

    // Runs the event of a source that depends on the index through the handler, with a body of its own.
    private async Task<(int StatusCode, string Body)> ServeAsync(InngestLambdaHandler handler, int index)
    {
        switch (index % 3)
        {
            case 0:
                var rest = await handler.HandleAsync(LambdaEvents.Rest(query: LambdaEvents.CallQuery, body: BodyOf(index)), _lambda.Context);
                return (rest.StatusCode, rest.Body);
            case 1:
                var http = await handler.HandleAsync(LambdaEvents.HttpApi(body: BodyOf(index)), _lambda.Context);
                return (http.StatusCode, http.Body);
            default:
                var url = await handler.HandleAsync(LambdaEvents.FunctionUrl(body: BodyOf(index)), _lambda.Context);
                return (url.StatusCode, url.Body);
        }
    }

    private static string BodyOf(int index) => $$"""{"index":{{index}}}""";

    // Holds every call inside the engine until released, and then answers each with the body it sent, read only after
    // the wait.
    private sealed class GatedEngine(int expected) : IExecutionEngine
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _running;

        public Task AllEntered => _entered.Task;

        public int Running => Volatile.Read(ref _running);

        public void Release() => _gate.SetResult();

        public async Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _running) == expected)
            {
                _entered.SetResult();
            }

            await _gate.Task.ConfigureAwait(false);
            return FakeExecutionEngine.Result(200, Encoding.UTF8.GetString(request.Body.Span));
        }
    }
}
