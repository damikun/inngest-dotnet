namespace Inngest.Tests.Execution;

public sealed class UseApiTests : IDisposable
{
    private readonly EngineHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Large_runs_load_their_steps_and_events_from_the_api()
    {
        var state = Call.New()
            .Event("""{"data":{"value":7},"name":"test/event"}""")
            .Data("A", "\"from api\"");
        _host.Api.Steps = state.StepsJson();
        _host.Api.Events = state.EventsJson();
        var function = TestFunction.Define(async context =>
        {
            var a = await context.Step.RunAsync("A", () => "executed");
            return $"{a} {context.Event.Data.GetProperty("value").GetInt32()}";
        });

        var result = await _host.CallAsync(function, state.UseApi());

        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe("\"from api 7\"");
        _host.Api.Requests.ShouldBe(["actions:01JRUN", "batch:01JRUN"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_failure_to_load_the_run_state_is_retried()
    {
        _host.Api.Failure = new HttpRequestException("Inngest is unreachable");
        var function = TestFunction.Define(_ => Task.FromResult<object?>("never runs"));

        var result = await _host.CallAsync(function, Call.New().UseApi());

        result.StatusCode.ShouldBe(500);
        result.NoRetry.ShouldBe(false);
        result.Json.GetProperty("type").GetString().ShouldBe("internal");
        result.Json.GetProperty("message").GetString().ShouldBe("Inngest is unreachable");
        _host.Logs.GetSnapshot().ShouldContain(record => record.Id.Id == 103);
    }

    [Fact]
    public async Task Run_state_of_the_wrong_shape_is_an_internal_error()
    {
        _host.Api.Steps = "[]";
        _host.Api.Events = "[]";
        var function = TestFunction.Define(_ => Task.FromResult<object?>("never runs"));

        var result = await _host.CallAsync(function, Call.New().UseApi());

        result.StatusCode.ShouldBe(500);
        result.Json.GetProperty("type").GetString().ShouldBe("internal");
    }

    [Fact]
    public async Task Calls_without_use_api_do_not_touch_the_api()
    {
        var function = TestFunction.Define(_ => Task.FromResult<object?>(null));

        await _host.CallAsync(function, Call.New());

        _host.Api.Requests.ShouldBeEmpty();
    }
}
