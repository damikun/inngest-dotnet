using Inngest.Configuration;
using Microsoft.Extensions.Logging;

namespace Inngest.Tests.Execution;

public sealed class RunUrlTests : IDisposable
{
    private static readonly InngestSettings DevSettings = EngineHost.Settings with { RunUiUrl = new Uri("http://localhost:8288/") };

    private readonly EngineHost _host = new(settings: DevSettings);

    public void Dispose() => _host.Dispose();

    [Fact]
    public void In_dev_mode_a_run_has_a_page_on_the_dev_server()
    {
        var settings = InngestSettings.Create(new InngestOptions { AppId = "app", IsDev = true }, _ => null);

        settings.RunUrl("01JRUN")!.AbsoluteUri.ShouldBe("http://localhost:8288/run?runID=01JRUN");
    }

    [Fact]
    public void The_page_is_on_the_dev_server_the_app_is_configured_for()
    {
        var settings = InngestSettings.Create(new InngestOptions { AppId = "app", IsDev = true, DevServerUrl = new Uri("http://inngest:9999/some/path") }, _ => null);

        settings.RunUrl("01JRUN")!.AbsoluteUri.ShouldBe("http://inngest:9999/run?runID=01JRUN");
    }

    [Fact]
    public void An_INNGEST_DEV_address_is_used_too()
    {
        var settings = InngestSettings.Create(new InngestOptions { AppId = "app" }, name => name == "INNGEST_DEV" ? "http://devserver:8288" : null);

        settings.RunUrl("01JRUN")!.AbsoluteUri.ShouldBe("http://devserver:8288/run?runID=01JRUN");
    }

    [Fact]
    public void The_run_id_is_escaped()
    {
        DevSettings.RunUrl("a&b c")!.AbsoluteUri.ShouldBe("http://localhost:8288/run?runID=a%26b%20c");
    }

    [Fact]
    public void Cloud_mode_has_no_run_page_because_the_environment_slug_is_unknown()
    {
        var settings = InngestSettings.Create(new InngestOptions { AppId = "app", IsDev = false, Environment = "staging" }, _ => null);

        settings.RunUrl("01JRUN").ShouldBeNull();
    }

    [Fact]
    public async Task The_function_context_carries_the_run_url()
    {
        Uri? seen = null;
        var function = TestFunction.Define(context =>
        {
            seen = context.RunUrl;
            return Task.FromResult<object?>(null);
        });

        await _host.CallAsync(function, Call.New());

        seen!.AbsoluteUri.ShouldBe("http://localhost:8288/run?runID=01JRUN");
    }

    [Fact]
    public async Task Without_a_run_page_the_context_has_none()
    {
        using var host = new EngineHost();
        var seen = new Uri("http://unset");
        var function = TestFunction.Define(context =>
        {
            seen = context.RunUrl;
            return Task.FromResult<object?>(null);
        });

        await host.CallAsync(function, Call.New());

        seen.ShouldBeNull();
    }

    [Fact]
    public async Task The_call_scope_carries_the_run_url()
    {
        var function = TestFunction.Define(context =>
        {
            context.Logger.LogWarning("hello");
            return Task.FromResult<object?>(null);
        });

        await _host.CallAsync(function, Call.New());

        var scope = _host.LogsOf("Inngest.Function.fn").ShouldHaveSingleItem().Scopes.ShouldHaveSingleItem();
        ((IEnumerable<KeyValuePair<string, object?>>)scope!).ShouldContain(new KeyValuePair<string, object?>("runURL", "http://localhost:8288/run?runID=01JRUN"));
        scope!.ToString().ShouldBe("functionID:fn runID:01JRUN attempt:0 eventName:test/event runURL:http://localhost:8288/run?runID=01JRUN");
    }

    // The failure entries keep their messages and event ids, which a Sentry log filter may name; the scope adds the link.
    [Fact]
    public async Task A_step_that_fails_for_good_is_logged_inside_the_scope_with_the_run_url()
    {
        var function = TestFunction.Define(async context =>
        {
            await context.Step.RunAsync<string>("A", string () => throw new NonRetriableException("boom"));
            return null;
        });

        await _host.CallAsync(function, Call.New());

        var record = _host.LogsOf("Inngest.Function.fn").Single(record => record.Level == LogLevel.Error);
        record.Id.Id.ShouldBe(109);
        record.Message.ShouldBe("Step A of run 01JRUN of function fn failed on attempt 0 and is not retried.");
        record.Scopes.OfType<IEnumerable<KeyValuePair<string, object?>>>().SelectMany(scope => scope)
            .ShouldContain(new KeyValuePair<string, object?>("runURL", "http://localhost:8288/run?runID=01JRUN"));
    }
}
