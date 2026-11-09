using Inngest.Functions;
using Inngest.Registration;
using Inngest.Tests.Execution;
using Microsoft.Extensions.DependencyInjection;
using static Inngest.Tests.Registration.RegistryFixtures;

namespace Inngest.Tests.Registration;

// A cancel handler is registered the way inngest/inngest-js#1800 registers onCancel: a companion function
// "<id>-cancel", triggered by inngest/function.cancelled for this function, with one retry.
public sealed class CancelHandlerTests
{
    [Fact]
    public void Registers_the_handler_as_a_function_of_its_own_that_runs_when_a_run_is_cancelled()
    {
        var registry = new FunctionRegistry(WithCancelHandler(Orders with { Retries = 3 }), Settings());

        var body = Write(registry);

        FunctionIds(body).ShouldBe(["my-app-orders", "my-app-orders-cancel"]);
        FunctionAt(body, 1).ShouldBe(
            """{"id":"my-app-orders-cancel","name":"orders (cancel)","triggers":[{"event":"inngest/function.cancelled","expression":"event.data.function_id == 'my-app-orders'"}],"steps":{"step":{"id":"step","name":"step","runtime":{"type":"http","url":"https://example.com/api/inngest?fnId=my-app-orders-cancel&stepId=step"},"retries":{"attempts":1}}}}""");
    }

    [Fact]
    public void Quotes_in_the_function_id_are_escaped_in_the_expression()
    {
        var registry = new FunctionRegistry(WithCancelHandler(Orders with { Id = @"it's\fine" }), Settings());

        var expression = FunctionElement(Write(registry), 1).GetProperty("triggers")[0].GetProperty("expression").GetString();

        expression.ShouldBe(@"event.data.function_id == 'my-app-it\'s\\fine'");
    }

    [Fact]
    public void Handlers_of_connect_apps_use_the_websocket_runtime_too()
    {
        var registry = new FunctionRegistry(WithCancelHandler(Orders), Settings());

        var body = Write(registry, "wss://connect", "connect", "connect");

        FunctionElement(body, 1).GetProperty("steps").GetProperty("step").GetProperty("runtime").GetRawText().ShouldBe(
            """{"type":"ws","url":"wss://connect/?fnId=my-app-orders-cancel&stepId=step"}""");
    }

    [Fact]
    public void A_class_that_handles_failures_and_cancellations_gets_both_companions()
    {
        var registry = RegistryOf(inngest => inngest.AddFunction<HandlesBoth>(Orders));

        FunctionIds(Write(registry)).ShouldBe(["my-app-orders", "my-app-orders-failure", "my-app-orders-cancel"]);
        registry.TryGet("my-app-orders-cancel", out var handler).ShouldBeTrue();
        handler.CancelHandlerFor.ShouldBe("orders");
        handler.FailureHandlerFor.ShouldBeNull();
    }

    [Fact]
    public void A_delegate_function_takes_its_cancel_handler_as_onCancel()
    {
        var registry = RegistryOf(inngest => inngest.AddFunction(
            Orders,
            (_, _) => Task.FromResult<object?>(null),
            onCancel: (_, _) => Task.CompletedTask));

        FunctionIds(Write(registry)).ShouldBe(["my-app-orders", "my-app-orders-cancel"]);
    }

    [Fact]
    public void A_function_without_a_handler_gets_no_companion()
    {
        var registry = RegistryOf(inngest => inngest.AddFunction(Orders, (_, _) => Task.FromResult<object?>(null)));

        FunctionIds(Write(registry)).ShouldBe(["my-app-orders"]);
    }

    [Fact]
    public async Task The_handler_runs_with_the_cancellation_and_can_use_steps()
    {
        using var host = new EngineHost();
        FunctionCancelled? cancelled = null;
        var function = new FunctionDefinition(
            CancelFunction.CreateOptions(new FunctionOptions { Id = "charge", Triggers = [] }),
            _ => new CancelFunction(async (context, _) =>
            {
                cancelled = context.Event.Data;
                await context.Step.RunAsync("release", () => "released");
            }))
        { CancelHandlerFor = "charge" };
        var call = Call.New().Event(
            """{"name":"inngest/function.cancelled","data":{"function_id":"test-app-charge","run_id":"01JCANCELLED","event":{"name":"shop/order.placed","data":{"orderId":"o_1"}}}}""");

        var first = await host.CallAsync(function, call);
        var second = await host.CallAsync(function, call.Data("release", "\"released\""));

        first.DisplayNames.ShouldBe(["release"]);
        second.StatusCode.ShouldBe(200);
        cancelled.ShouldNotBeNull();
        cancelled.FunctionId.ShouldBe("test-app-charge");
        cancelled.RunId.ShouldBe("01JCANCELLED");
        cancelled.Event.GetProperty("data").GetProperty("orderId").GetString().ShouldBe("o_1");
    }

    private static FunctionDefinition[] WithCancelHandler(FunctionOptions options) =>
    [
        Define(options),
        new(CancelFunction.CreateOptions(options), static _ => throw new NotSupportedException("The registry does not run functions."))
        {
            CancelHandlerFor = options.Id,
        },
    ];

    private static IFunctionRegistry RegistryOf(Action<IInngestBuilder> configure)
    {
        var services = new ServiceCollection();
        configure(services.AddInngest(options => options.AppId = "my-app"));
        return services.BuildServiceProvider().GetRequiredService<IFunctionRegistry>();
    }

    public sealed class HandlesBoth : InngestFunction<object>, IFailureHandler, ICancelHandler
    {
        public override Task<object?> ExecuteAsync(FunctionContext<object> context, CancellationToken cancellationToken) =>
            Task.FromResult<object?>(null);

        public Task OnFailureAsync(FunctionContext<FunctionFailure> context, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task OnCancelAsync(FunctionContext<FunctionCancelled> context, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
