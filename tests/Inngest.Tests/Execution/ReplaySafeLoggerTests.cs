using Inngest.Functions;
using Microsoft.Extensions.Logging;

namespace Inngest.Tests.Execution;

public sealed class ReplaySafeLoggerTests : IDisposable
{
    private const string Category = "Inngest.Function.fn";

    private readonly EngineHost _host = new();

    public void Dispose() => _host.Dispose();

    // Port of "can use built-in logger middleware": each line is written once per run.
    [Fact]
    public async Task Messages_are_written_once_across_replays()
    {
        var function = LoggingFunction();

        await _host.CallAsync(function, Call.New());
        var first = Messages();
        _host.Logs.Clear();
        await _host.CallAsync(function, Call.New().Data("A", "\"A\""));
        var second = Messages();
        _host.Logs.Clear();
        await _host.CallAsync(function, Call.New().Data("A", "\"A\"").Data("B", "\"B\""));
        var third = Messages();

        first.ShouldBe(["info1", "A"]);
        second.ShouldBe(["2", "B"]);
        third.ShouldBe(["3"]);
    }

    [Fact]
    public async Task The_logger_reports_itself_disabled_while_replaying()
    {
        var enabled = new List<bool>();
        var function = TestFunction.Define(async context =>
        {
            enabled.Add(context.Logger.IsEnabled(LogLevel.Information));
            await context.Step.RunAsync("A", () => "A");
            enabled.Add(context.Logger.IsEnabled(LogLevel.Information));
            return null;
        });

        await _host.CallAsync(function, Call.New().Data("A", "\"A\""));

        enabled.ShouldBe([false, true]);
    }

    [Fact]
    public async Task Messages_carry_the_run_id_and_event_name()
    {
        var function = TestFunction.Define(context =>
        {
            context.Logger.LogWarning("hello");
            return Task.FromResult<object?>(null);
        });

        await _host.CallAsync(function, Call.New());

        var record = _host.LogsOf(Category).ShouldHaveSingleItem();
        record.Level.ShouldBe(LogLevel.Warning);
        var scope = record.Scopes.OfType<IEnumerable<KeyValuePair<string, object?>>>().ShouldHaveSingleItem();
        scope.ShouldBe([new("runID", "01JRUN"), new("eventName", "test/event")]);
        record.Scopes.Single()!.ToString().ShouldBe("runID:01JRUN eventName:test/event");
    }

    [Fact]
    public async Task Each_function_logs_under_its_own_category()
    {
        var function = TestFunction.Define(
            context =>
            {
                context.Logger.LogInformation("from billing");
                return Task.FromResult<object?>(null);
            },
            id: "billing");

        await _host.CallAsync(function, Call.New());

        _host.LogsOf("Inngest.Function.billing").ShouldHaveSingleItem().Message.ShouldBe("from billing");
    }

    [Fact]
    public async Task A_targeted_call_logs_from_the_requested_step()
    {
        var function = TestFunction.Define(async context =>
        {
            await Task.WhenAll(
                context.Step.RunAsync("A", () => Log(context, "A")),
                context.Step.RunAsync("B", () => Log(context, "B")));
            return null;
        });

        await _host.CallAsync(function, Call.New().Data("A", "\"A\"").DisableImmediateExecution(), stepId: Call.Hash("B"));

        Messages().ShouldBe(["B"]);
    }

    private FunctionDefinition LoggingFunction() => TestFunction.Define(async context =>
    {
        context.Logger.LogInformation("info1");
        await context.Step.RunAsync("A", () => Log(context, "A"));
        context.Logger.LogInformation("2");
        await context.Step.RunAsync("B", () => Log(context, "B"));
        context.Logger.LogInformation("3");
        return null;
    });

    private static string Log(FunctionContext context, string message)
    {
        context.Logger.LogInformation("{Message}", message);
        return message;
    }

    private string[] Messages() => [.. _host.LogsOf(Category).Select(record => record.Message)];
}
