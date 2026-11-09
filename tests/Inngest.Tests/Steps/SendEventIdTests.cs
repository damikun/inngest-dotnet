using System.Net;
using System.Text.Json;
using Inngest.Client;
using Inngest.Execution;
using Inngest.Functions;
using Inngest.Tests.Client;
using Inngest.Tests.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Inngest.Tests.Steps;

// Events a step sends get ids made of the run, the step and their place in the list, so that a step that runs again
// after its result was lost sends them under the same ids and Inngest does not start their runs twice.
public sealed class SendEventIdTests
{
    private readonly FakeHttpHandler _handler = new();

    [Fact]
    public async Task Events_without_an_id_get_one_from_the_run_the_step_and_their_index()
    {
        _handler.RespondAlways(HttpStatusCode.OK, ClientFixture.Accepted);
        await using var services = Build();
        var function = TestFunction.Define(async context => await context.Step.SendEventAsync("notify",
        [
            new InngestEvent("app/a"),
            new InngestEvent("app/b") { Id = "chosen-by-the-code" },
            new InngestEvent("app/c"),
        ]));

        await CallAsync(services, function, Call.New());

        SentIds(0).ShouldBe([$"01JRUN-{Call.Hash("notify")}-0", "chosen-by-the-code", $"01JRUN-{Call.Hash("notify")}-2"]);
    }

    [Fact]
    public async Task A_step_that_runs_again_sends_its_events_under_the_same_ids()
    {
        _handler.RespondAlways(HttpStatusCode.OK, ClientFixture.Accepted);
        await using var services = Build();
        var function = TestFunction.Define(async context => await context.Step.SendEventAsync("notify", new InngestEvent("app/a")));

        await CallAsync(services, function, Call.New());
        await CallAsync(services, function, Call.New().Attempt(1));

        SentIds(1).ShouldBe(SentIds(0));
    }

    [Fact]
    public async Task Steps_that_share_an_id_send_under_different_ids()
    {
        _handler.RespondAlways(HttpStatusCode.OK, ClientFixture.Accepted);
        await using var services = Build();
        var function = TestFunction.Define(async context =>
        {
            await context.Step.SendEventAsync("notify", new InngestEvent("app/a"));
            return await context.Step.SendEventAsync("notify", new InngestEvent("app/a"));
        });

        await CallAsync(services, function, Call.New().Data("notify", """{"ids":["01HQ0000000000000000000001"]}"""));

        SentIds(0).ShouldBe([$"01JRUN-{Call.Hash("notify:1")}-0"]);
    }

    [Fact]
    public async Task A_step_with_more_events_than_inngest_takes_fails_without_retries_and_sends_nothing()
    {
        await using var services = Build();
        var events = Enumerable.Range(0, InngestClient.MaxEventsPerSend + 1).Select(static _ => new InngestEvent("app/a")).ToArray();
        var function = TestFunction.Define(async context => await context.Step.SendEventAsync("notify", events));

        var result = await CallAsync(services, function, Call.New());

        result.OpCodes.ShouldBe(["StepFailed"]);
        result.NoRetry.ShouldBe(true);
        result.Op().GetProperty("error").GetProperty("message").GetString()
            .ShouldBe("Inngest accepts at most 5,000 events in one send, but this one has 5,001. Send them in several calls.");
        _handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_send_outside_a_run_with_more_events_than_inngest_takes_is_refused()
    {
        await using var services = Build();
        var events = Enumerable.Range(0, InngestClient.MaxEventsPerSend + 1).Select(static _ => new InngestEvent("app/a"));

        var exception = await Should.ThrowAsync<ArgumentException>(
            () => services.GetRequiredService<IInngestClient>().SendAsync(events, TestContext.Current.CancellationToken));

        exception.Message.ShouldStartWith("Inngest accepts at most 5,000 events in one send");
        _handler.Requests.ShouldBeEmpty();
    }

    private string[] SentIds(int request)
    {
        using var body = JsonDocument.Parse(_handler.Requests[request].BodyText);
        return [.. body.RootElement.EnumerateArray().Select(static @event => @event.TryGetProperty("id", out var id) ? id.GetString()! : "")];
    }

    private ServiceProvider Build() => ClientServices.Build(_handler, new FakeTimeProvider());

    private static async Task<CallResult> CallAsync(IServiceProvider services, FunctionDefinition function, Call call)
    {
        using var host = new EngineHost();
        return await EngineHost.ResultOf(host.Engine.ExecuteAsync(
            new ExecutionRequest { Function = function, Body = call.ToBytes(), StepId = "step", Services = services },
            TestContext.Current.CancellationToken));
    }
}
