using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Inngest.Tests.Execution;

// User exceptions never reach the host, because the engine turns them into answers for Inngest. These tests check
// that the engine reports them where hosts look: the log and the current activity.
public sealed class ErrorReportingTests : IDisposable
{
    private readonly EngineHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task A_function_that_throws_is_logged_as_a_warning_while_Inngest_retries_it()
    {
        var function = TestFunction.Define(_ => throw new InvalidOperationException("boom"));

        await _host.CallAsync(function, Call.New().Attempt(1, maxAttempts: 4));

        var record = _host.Logs.GetSnapshot().Where(record => record.Id.Id == 106).ShouldHaveSingleItem();
        record.Level.ShouldBe(LogLevel.Warning);
        record.Category.ShouldBe("Inngest.Function.fn");
        record.Exception.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("boom");
        record.Message.ShouldBe("Run 01JRUN of function fn threw on attempt 1; Inngest retries it.");
    }

    [Fact]
    public async Task A_function_that_fails_for_good_is_logged_as_an_error()
    {
        var function = TestFunction.Define(_ => throw new NonRetriableException("no"));

        await _host.CallAsync(function, Call.New());

        var record = _host.Logs.GetSnapshot().Where(record => record.Id.Id == 107).ShouldHaveSingleItem();
        record.Level.ShouldBe(LogLevel.Error);
        record.Exception.ShouldBeOfType<NonRetriableException>();
    }

    [Fact]
    public async Task The_last_attempt_of_a_function_is_logged_as_an_error()
    {
        var function = TestFunction.Define(_ => throw new InvalidOperationException("boom"));

        await _host.CallAsync(function, Call.New().Attempt(3, maxAttempts: 4));

        _host.Logs.GetSnapshot().Where(record => record.Id.Id == 107).ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Error);
    }

    [Fact]
    public async Task A_step_that_throws_is_logged_with_its_id_while_Inngest_retries_it()
    {
        var function = TestFunction.Define(async context =>
            await context.Step.RunAsync<int>("charge", _ => throw new TimeoutException("slow")));

        await _host.CallAsync(function, Call.New());

        var record = _host.Logs.GetSnapshot().Where(record => record.Id.Id == 108).ShouldHaveSingleItem();
        record.Level.ShouldBe(LogLevel.Warning);
        record.Exception.ShouldBeOfType<TimeoutException>();
        record.Message.ShouldBe("Step charge of run 01JRUN of function fn threw on attempt 0; Inngest retries it.");
    }

    [Fact]
    public async Task A_step_that_fails_for_good_is_logged_as_an_error()
    {
        var function = TestFunction.Define(async context =>
            await context.Step.RunAsync<int>("charge", _ => throw new NonRetriableException("card declined")));

        await _host.CallAsync(function, Call.New());

        var record = _host.Logs.GetSnapshot().Where(record => record.Id.Id == 109).ShouldHaveSingleItem();
        record.Level.ShouldBe(LogLevel.Error);
        record.Exception.ShouldBeOfType<NonRetriableException>();
    }

    [Fact]
    public async Task Errors_mark_the_activity_of_the_call()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == nameof(ErrorReportingTests),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource(nameof(ErrorReportingTests));
        var function = TestFunction.Define(_ => throw new InvalidOperationException("boom"));

        using var activity = source.StartActivity("call");
        await _host.CallAsync(function, Call.New());

        activity.ShouldNotBeNull();
        activity.Status.ShouldBe(ActivityStatusCode.Error);
        activity.StatusDescription.ShouldBe("boom");
        var exception = activity.Events.ShouldHaveSingleItem();
        exception.Name.ShouldBe("exception");
        exception.Tags.ShouldContain(tag => tag.Key == "exception.type" && Equals(tag.Value, typeof(InvalidOperationException).FullName));
    }

    [Fact]
    public async Task A_run_that_succeeds_logs_no_errors()
    {
        var function = TestFunction.Define(async context => await context.Step.RunAsync("ok", () => 1));

        await _host.CallAsync(function, Call.New());

        _host.Logs.GetSnapshot().ShouldNotContain(record => record.Id.Id >= 106 && record.Id.Id <= 109);
    }
}
