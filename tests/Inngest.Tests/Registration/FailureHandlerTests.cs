using Inngest.Registration;
using static Inngest.Tests.Registration.RegistryFixtures;

namespace Inngest.Tests.Registration;

public sealed class FailureHandlerTests
{
    [Fact]
    public void Registers_the_handler_as_a_function_of_its_own_that_runs_when_the_function_fails()
    {
        var registry = new FunctionRegistry(WithFailureHandler(Orders with { Retries = 3 }), Settings());

        var body = Write(registry);

        FunctionIds(body).ShouldBe(["my-app-orders", "my-app-orders-failure"]);
        FunctionAt(body, 1).ShouldBe(
            """{"id":"my-app-orders-failure","name":"orders (failure)","triggers":[{"event":"inngest/function.failed","expression":"event.data.function_id == 'my-app-orders'"}],"steps":{"step":{"id":"step","name":"step","runtime":{"type":"http","url":"https://example.com/api/inngest?fnId=my-app-orders-failure&stepId=step"},"retries":{"attempts":1}}}}""");
    }

    [Fact]
    public void The_function_itself_is_registered_unchanged()
    {
        var withHandler = Write(new FunctionRegistry(WithFailureHandler(Orders), Settings()));
        var without = Write(Registry(Orders));

        FunctionAt(withHandler, 0).ShouldBe(FunctionAt(without, 0));
    }

    [Fact]
    public void The_handler_is_named_after_the_function_it_handles()
    {
        var registry = new FunctionRegistry(WithFailureHandler(Orders with { Name = "Process orders" }), Settings());

        FunctionElement(Write(registry), 1).GetProperty("name").GetString().ShouldBe("Process orders (failure)");
    }

    [Fact]
    public void The_trigger_names_the_function_by_app_id_and_function_id()
    {
        var registry = new FunctionRegistry(WithFailureHandler(Orders with { Id = "Send Email" }), Settings("Shop App"));

        RawProperty(FunctionElement(Write(registry), 1), "triggers").ShouldBe(
            """[{"event":"inngest/function.failed","expression":"event.data.function_id == 'Shop App-Send Email'"}]""");
    }

    [Fact]
    public void Quotes_in_the_function_id_are_escaped_in_the_expression()
    {
        var registry = new FunctionRegistry(WithFailureHandler(Orders with { Id = @"it's\fine" }), Settings());

        var expression = FunctionElement(Write(registry), 1).GetProperty("triggers")[0].GetProperty("expression").GetString();

        expression.ShouldBe(@"event.data.function_id == 'my-app-it\'s\\fine'");
    }

    [Fact]
    public void The_handler_takes_no_flow_control_from_the_function()
    {
        var limited = Orders with
        {
            Concurrency = [new ConcurrencyLimit(1)],
            Throttle = new Throttle(1, TimeSpan.FromMinutes(1)),
            Idempotency = "event.data.orderId",
        };
        var registry = new FunctionRegistry(WithFailureHandler(limited), Settings());

        FunctionElement(Write(registry), 1).EnumerateObject().Select(property => property.Name)
            .ShouldBe(["id", "name", "triggers", "steps"]);
    }

    [Fact]
    public void Looks_the_handler_up_by_its_own_composite_id()
    {
        var registry = new FunctionRegistry(WithFailureHandler(Orders), Settings());

        registry.TryGet("my-app-orders-failure", out var handler).ShouldBeTrue();

        handler.FailureHandlerFor.ShouldBe("orders");
        registry.Functions.Count.ShouldBe(2);
    }

    [Fact]
    public void Handlers_of_connect_apps_use_the_websocket_runtime_too()
    {
        var registry = new FunctionRegistry(WithFailureHandler(Orders), Settings());

        var body = Write(registry, "wss://connect", "connect", "connect");

        FunctionElement(body, 1).GetProperty("steps").GetProperty("step").GetProperty("runtime").GetRawText().ShouldBe(
            """{"type":"ws","url":"wss://connect/?fnId=my-app-orders-failure&stepId=step"}""");
    }

    [Fact]
    public void Keeps_the_triggers_a_handler_declares_and_adds_the_failure_trigger_last()
    {
        var declared = Orders with { Id = "orders-failure", Triggers = [Trigger.Event("order/audited")] };
        var registry = new FunctionRegistry([Define(declared, failureHandlerFor: "orders")], Settings());

        RawProperty(FunctionElement(Write(registry), 0), "triggers").ShouldBe(
            """[{"event":"order/audited"},{"event":"inngest/function.failed","expression":"event.data.function_id == 'my-app-orders'"}]""");
    }

    [Fact]
    public void The_failure_trigger_counts_towards_the_trigger_limit()
    {
        var full = Orders with
        {
            Id = "orders-failure",
            Triggers = [.. Enumerable.Range(0, 10).Select(i => Trigger.Event($"order/{i}"))],
        };

        var exception = Should.Throw<InvalidOperationException>(
            () => new FunctionRegistry([Define(full, failureHandlerFor: "orders")], Settings()));

        exception.Message.ShouldBe("Function \"orders-failure\": A function can have at most 10 triggers, but has 11.");
    }
}
