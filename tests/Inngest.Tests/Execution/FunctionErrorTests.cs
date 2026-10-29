using System.Text.Json;
using Inngest.Execution;
using Inngest.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Inngest.Tests.Execution;

public sealed class FunctionErrorTests : IDisposable
{
    private readonly EngineHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task A_function_canceled_by_its_own_code_reports_the_exception_that_canceled_it()
    {
        var function = TestFunction.Define(_ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing."));

        var result = await _host.CallAsync(function, Call.New());

        result.StatusCode.ShouldBe(500);
        result.NoRetry.ShouldBe(false);
        result.Json.GetProperty("name").GetString().ShouldBe("TaskCanceledException");
        result.Json.GetProperty("message").GetString()!.ShouldContain("HttpClient.Timeout");
    }

    [Fact]
    public async Task Event_data_that_does_not_fit_the_event_type_fails_the_run_without_retries()
    {
        var function = TestFunction.Define<Order>(_ => Task.FromResult<object?>(null));

        var result = await _host.CallAsync(function, Call.New().Event("""{"name":"shop/order","data":{"total":"not a number"}}"""));

        result.StatusCode.ShouldBe(400);
        result.NoRetry.ShouldBe(true);
        result.Json.GetProperty("name").GetString().ShouldBe("JsonException");
        result.Json.TryGetProperty("type", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Retry_delays_are_capped_at_the_day_inngest_allows()
    {
        var function = TestFunction.Define(_ => throw new RetryAfterException("much later", TimeSpan.FromDays(30)));

        var result = await _host.CallAsync(function, Call.New());

        result.RetryAfter.ShouldBe("86400");
    }

    [Fact]
    public async Task The_context_carries_the_cancellation_token_of_the_call()
    {
        var seen = CancellationToken.None;
        var function = TestFunction.Define(context =>
        {
            seen = context.CancellationToken;
            return Task.FromResult<object?>(null);
        });

        await _host.CallAsync(function, Call.New());

        seen.ShouldBe(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Steps_without_a_result_replay_with_only_source_generated_serializer_metadata()
    {
        // The app's resolver knows only its own types, as in a trimmed or Native AOT app.
        var settings = InngestSettings.Create(
            new InngestOptions
            {
                AppId = "test-app",
                IsDev = true,
                SerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = OrderJsonContext.Default },
            },
            _ => null);
        await using var services = new ServiceCollection().BuildServiceProvider();
        var engine = new ExecutionEngine(settings, NullLoggerFactory.Instance, TimeProvider.System, _host.Api);
        var function = TestFunction.Define(async context =>
        {
            await context.Step.RunAsync("notify", () => { });
            return null;
        });

        using var result = await engine.ExecuteAsync(
            new ExecutionRequest { Function = function, Body = Call.New().Memo("notify", """{"data":null}""").ToBytes(), StepId = "step", Services = services },
            TestContext.Current.CancellationToken);

        result.StatusCode.ShouldBe(200);
    }

    public sealed record Order(int Total);
}

[System.Text.Json.Serialization.JsonSerializable(typeof(FunctionErrorTests.Order))]
internal sealed partial class OrderJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
