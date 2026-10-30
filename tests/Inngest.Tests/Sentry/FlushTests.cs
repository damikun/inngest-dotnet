using Inngest.Tests.Execution;

namespace Inngest.Tests.Sentry;

// When the call hands its events to Sentry, and the hub it does that with.
[Collection(SentryHost.Collection)]
public sealed class FlushTests
{
    [Fact]
    public async Task A_call_is_flushed_when_it_ends_and_its_events_have_been_sent_by_then()
    {
        await using var host = new SentryHost(registerHub: true);
        var function = TestFunction.Define(_ => throw new NonRetriableException("no"));

        await host.CallAsync(function, Call.New());

        host.Events.ShouldHaveSingleItem();
        host.Sent.Count.ShouldBe(1);
        var flushes = host.Spy!.Calls.Where(call => call.Method == "FlushAsync").ToList();
        flushes.Count.ShouldBe(1);
        flushes[0].Arguments.ShouldBe([TimeSpan.FromSeconds(2)]);
        host.Spy.Methods.ToList().LastIndexOf("CaptureEvent").ShouldBeLessThan(host.Spy.Methods.ToList().IndexOf("FlushAsync"));
    }

    [Fact]
    public async Task A_call_that_reports_nothing_is_flushed_as_well()
    {
        await using var host = new SentryHost(registerHub: true);
        var function = TestFunction.Define(_ => Task.FromResult<object?>("done"));

        (await host.CallAsync(function, Call.New())).ShouldBe(200);

        host.Spy!.Methods.Count(method => method == "FlushAsync").ShouldBe(1);
    }

    [Fact]
    public async Task The_flush_waits_as_long_as_the_options_say()
    {
        await using var host = new SentryHost(options => options.FlushTimeout = TimeSpan.FromSeconds(7), registerHub: true);
        var function = TestFunction.Define(_ => Task.FromResult<object?>("done"));

        await host.CallAsync(function, Call.New());

        host.Spy!.Calls.Single(call => call.Method == "FlushAsync").Arguments.ShouldBe([TimeSpan.FromSeconds(7)]);
    }

    [Fact]
    public async Task A_call_is_not_flushed_when_that_is_left_to_Sentry()
    {
        await using var host = new SentryHost(options => options.DisableAutomaticFlush = true, registerHub: true);
        var function = TestFunction.Define(_ => throw new NonRetriableException("no"));

        await host.CallAsync(function, Call.New());

        host.Events.ShouldHaveSingleItem();
        host.Spy!.Methods.ShouldNotContain("FlushAsync");
    }

    [Fact]
    public async Task The_hub_the_app_registered_is_the_one_the_call_reports_with()
    {
        await using var host = new SentryHost(registerHub: true);
        var function = TestFunction.Define(_ => throw new NonRetriableException("no"));

        await host.CallAsync(function, Call.New());

        host.Spy!.Methods.ShouldContain("PushScope");
        host.Spy.Methods.ShouldContain("ContinueTrace");
        host.Spy.Methods.ShouldContain("CaptureEvent");
    }

    [Fact]
    public async Task Nothing_is_flushed_while_Sentry_is_not_enabled()
    {
        await using var host = new SentryHost(initialize: false, registerHub: true);
        var function = TestFunction.Define(_ => throw new NonRetriableException("no"));

        await host.CallAsync(function, Call.New());

        host.Spy!.Methods.ShouldNotContain("FlushAsync");
        host.Spy.Methods.ShouldNotContain("CaptureEvent");
    }
}
