using Inngest.IntegrationTests.Functions;
using Inngest.IntegrationTests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests;

// Typed events, functions configured by attributes and invoke by class, through the dev server: the app sends with
// SendAsync<T>, the server runs the attribute class the type triggers, and that class invokes another one by its
// class and sends a typed event from a step.
public sealed class TypedApiTests(DevServerFixture fixture) : IntegrationTest<DevServerFixture>(fixture)
{
    private IInngestClient Client => Fixture.App.Services.GetRequiredService<IInngestClient>();

    [Fact(Timeout = 30_000)]
    public async Task A_typed_send_runs_the_attribute_class_its_type_triggers_which_invokes_another_by_class()
    {
        var sent = await Client.SendAsync(new TypedOrderPlaced(TestId, 21), Cancellation);

        var run = await Api.WaitForRunEndAsync(sent.Ids.ShouldHaveSingleItem(), Cancellation);

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<TypedOrderResult>().Quote.ShouldBe(new PriceQuote(42, "EUR"));
        Observed.Values<int>("priced").ShouldBe([21]);
    }

    [Fact(Timeout = 30_000)]
    public async Task A_typed_send_step_runs_the_function_of_its_type_and_carries_its_version()
    {
        var sent = await Client.SendAsync(new TypedOrderPlaced(TestId, 7), Cancellation);
        var order = await Api.WaitForRunEndAsync(sent.Ids[0], Cancellation);

        var confirmation = await Api.WaitForRunEndAsync(order.OutputAs<TypedOrderResult>().ConfirmationId, Cancellation);

        confirmation.Status.ShouldBe(RunStatus.Completed);
        confirmation.OutputAs<int>().ShouldBe(14);
        Observed.Values<string>("confirmed").ShouldBe(["14 v2026-10-29"]);
    }

    [Fact(Timeout = 30_000)]
    public async Task Several_typed_events_go_out_in_one_send()
    {
        var sent = await Client.SendAsync([new TypedOrderPlaced(TestId, 1), new TypedOrderPlaced(TestId, 2)], Cancellation);

        sent.Ids.Count.ShouldBe(2);
        foreach (var eventId in sent.Ids)
        {
            (await Api.WaitForRunEndAsync(eventId, Cancellation)).Status.ShouldBe(RunStatus.Completed);
        }

        Observed.Values<int>("priced").Order().ShouldBe([1, 2]);
    }

    [Fact(Timeout = 30_000)]
    public async Task An_attribute_class_is_synced_with_the_options_its_attributes_give()
    {
        var functions = (await Api.GetAppFunctionsAsync(TestFunctions.AppId, Cancellation)).ToDictionary(function => function.Slug);

        var order = functions["itests-typed-order"];
        order.Name.ShouldBe("Typed order");
        order.Retries.ShouldBe(1);
        order.Config.ShouldContainJson("""
            {
              "triggers": [{ "event": "itests/typed.order-placed" }],
              "concurrency": [{ "limit": 5, "key": "event.data.testId" }],
              "cancel": [{ "event": "itests/typed.order-cancelled", "timeout": "10m", "if": "event.data.testId == async.data.testId" }]
            }
            """);
        functions["itests-typed-pricing"].Config.ShouldContainJson("""{ "triggers": [] }""");
        functions["itests-typed-confirmation"].Config.ShouldContainJson("""{ "triggers": [{ "event": "itests/typed.order-confirmed" }] }""");
    }
}
