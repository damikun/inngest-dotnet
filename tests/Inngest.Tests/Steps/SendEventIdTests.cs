using System.Net;
using System.Text.Json;
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
