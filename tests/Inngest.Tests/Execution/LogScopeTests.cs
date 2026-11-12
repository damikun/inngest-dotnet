using Inngest.Configuration;
using Inngest.Tests.Checkpointing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Inngest.Tests.Execution;

public sealed class LogScopeTests : IDisposable
{
    private readonly EngineHost _host = new(services => services.AddSingleton<Mailer>());

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task A_service_the_function_uses_logs_with_the_run_context()
    {
        var function = TestFunction.Define(context =>
        {
            context.Services.GetRequiredService<Mailer>().Send("body");
            return Task.FromResult<object?>(null);
        });

        await _host.CallAsync(function, Call.New().Attempt(2));

        Properties(MailerLog("body")).ShouldBe(new Dictionary<string, object?>
        {
            ["functionID"] = "fn",
            ["runID"] = "01JRUN",
            ["attempt"] = 2,
            ["eventName"] = "test/event",
        });
    }

    [Fact]
    public async Task A_service_a_step_uses_logs_with_the_step_id_as_well()
    {
        var function = TestFunction.Define(async context =>
        {
            await context.Step.RunAsync("A", () => context.Services.GetRequiredService<Mailer>().Send("A"));
            return null;
        });

        await _host.CallAsync(function, Call.New());

        var properties = Properties(MailerLog("A"));
        properties["runID"].ShouldBe("01JRUN");
        properties["stepID"].ShouldBe("A");
    }

    [Fact]
    public async Task The_step_scope_ends_with_the_handler()
    {
        var function = Checkpointed.Define(async context =>
        {
            var mailer = context.Services.GetRequiredService<Mailer>();
            await context.Step.RunAsync("A", async () =>
            {
                await Task.Yield();
                return mailer.Send("A");
            });
            mailer.Send("between");
            await context.Step.RunAsync("B", () => mailer.Send("B"));
            mailer.Send("after");
            return null;
        });

        await _host.CallCheckpointedAsync(function, Call.New());

        Properties(MailerLog("A"))["stepID"].ShouldBe("A");
        Properties(MailerLog("between")).ShouldNotContainKey("stepID");
        Properties(MailerLog("B"))["stepID"].ShouldBe("B");
        var after = Properties(MailerLog("after"));
        after.ShouldNotContainKey("stepID");
        after["runID"].ShouldBe("01JRUN");
    }

    [Fact]
    public async Task Calls_for_parallel_steps_running_at_once_each_log_with_their_own_step()
    {
        var bothStarted = new TaskCompletionSource();
        var started = 0;
        var function = TestFunction.Define(async context =>
        {
            var mailer = context.Services.GetRequiredService<Mailer>();
            await Task.WhenAll(
                context.Step.RunAsync("A", () => Send(mailer, "A")),
                context.Step.RunAsync("B", () => Send(mailer, "B")));
            return null;
        });

        // Each handler logs only once both are running, so their scopes overlap in time.
        async Task<string> Send(Mailer mailer, string message)
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                bothStarted.SetResult();
            }

            await bothStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
            return mailer.Send(message);
        }

        var call = Call.New().DisableImmediateExecution();
        await Task.WhenAll(_host.CallAsync(function, call, stepId: Call.Hash("A")), _host.CallAsync(function, call, stepId: Call.Hash("B")));

        Properties(MailerLog("A"))["stepID"].ShouldBe("A");
        Properties(MailerLog("B"))["stepID"].ShouldBe("B");
    }

    [Fact]
    public async Task A_targeted_call_scopes_the_requested_step()
    {
        var function = TestFunction.Define(async context =>
        {
            var mailer = context.Services.GetRequiredService<Mailer>();
            await Task.WhenAll(context.Step.RunAsync("A", () => mailer.Send("A")), context.Step.RunAsync("B", () => mailer.Send("B")));
            return null;
        });

        await _host.CallAsync(function, Call.New().DisableImmediateExecution(), stepId: Call.Hash("B"));

        Properties(MailerLog("B"))["stepID"].ShouldBe("B");
    }

    [Fact]
    public async Task A_numbered_step_logs_with_its_final_id()
    {
        var function = Checkpointed.Define(async context =>
        {
            var mailer = context.Services.GetRequiredService<Mailer>();
            await context.Step.RunAsync("send", () => mailer.Send("first"));
            await context.Step.RunAsync("send", () => mailer.Send("second"));
            return null;
        });

        await _host.CallCheckpointedAsync(function, Call.New());

        Properties(MailerLog("first"))["stepID"].ShouldBe("send");
        Properties(MailerLog("second"))["stepID"].ShouldBe("send:1");
    }

    [Fact]
    public async Task A_durable_endpoint_request_logs_with_the_run_context()
    {
        var function = TestFunction.Define(async context =>
        {
            var mailer = context.Services.GetRequiredService<Mailer>();
            mailer.Send("handler");
            await context.Step.RunAsync("A", () => mailer.Send("A"));
            return null;
        });

        await _host.EndpointAsync(function);

        var handler = Properties(MailerLog("handler"));
        handler["runID"].ShouldBe("01JENDPOINTRUN00000000000000");
        handler["functionID"].ShouldBe("fn");
        handler.ShouldNotContainKey("stepID");
        Properties(MailerLog("A"))["stepID"].ShouldBe("A");
    }

    [Fact]
    public async Task The_function_logger_carries_the_call_scope_instead_of_its_own()
    {
        var function = TestFunction.Define(context =>
        {
            context.Logger.LogWarning("hello");
            return Task.FromResult<object?>(null);
        });

        await _host.CallAsync(function, Call.New());

        var record = _host.LogsOf("Inngest.Function.fn").ShouldHaveSingleItem();
        record.Scopes.ShouldHaveSingleItem()!.ToString().ShouldBe("functionID:fn runID:01JRUN attempt:0 eventName:test/event");
    }

    [Fact]
    public async Task The_engine_logs_a_failed_step_inside_the_scopes()
    {
        var function = TestFunction.Define(async context =>
        {
            await context.Step.RunAsync<string>("A", string () => throw new InvalidOperationException("boom"));
            return null;
        });

        await _host.CallAsync(function, Call.New());

        var record = _host.Logs.GetSnapshot().Single(record => record.Level == LogLevel.Warning && record.Message.Contains("threw", StringComparison.Ordinal));
        Properties(record)["runID"].ShouldBe("01JRUN");
    }

    [Fact]
    public async Task Turned_off_no_scope_is_opened_and_the_function_logger_keeps_its_own()
    {
        using var host = new EngineHost(services => services.AddSingleton<Mailer>(), EngineHost.Settings with { LogScopes = false });
        var function = TestFunction.Define(async context =>
        {
            context.Logger.LogWarning("hello");
            await context.Step.RunAsync("A", () => context.Services.GetRequiredService<Mailer>().Send("A"));
            return null;
        });

        await host.CallAsync(function, Call.New());

        MailerLogs(host).ShouldHaveSingleItem().Scopes.ShouldBeEmpty();
        var scope = host.LogsOf("Inngest.Function.fn").ShouldHaveSingleItem().Scopes.ShouldHaveSingleItem();
        scope!.ToString().ShouldBe("runID:01JRUN eventName:test/event");
    }

    [Fact]
    public void The_option_is_on_unless_turned_off()
    {
        InngestSettings.Create(new InngestOptions { AppId = "app" }, _ => null).LogScopes.ShouldBeTrue();
        InngestSettings.Create(new InngestOptions { AppId = "app", LogScopes = false }, _ => null).LogScopes.ShouldBeFalse();
    }

    private FakeLogRecord MailerLog(string message) => MailerLogs(_host).Single(record => record.Message == message);

    private static IEnumerable<FakeLogRecord> MailerLogs(EngineHost host) =>
        host.Logs.GetSnapshot().Where(record => record.Category!.EndsWith(nameof(Mailer), StringComparison.Ordinal));

    // Every property of every scope the record was written in.
    private static Dictionary<string, object?> Properties(FakeLogRecord record)
    {
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var scope in record.Scopes.OfType<IEnumerable<KeyValuePair<string, object?>>>())
        {
            foreach (var (key, value) in scope)
            {
                properties[key] = value;
            }
        }

        return properties;
    }

    // A service with a logger of its own, as an app injects into the code its steps call.
    private sealed class Mailer(ILogger<Mailer> logger)
    {
        public string Send(string message)
        {
            logger.LogInformation("{Message}", message);
            return message;
        }
    }
}
