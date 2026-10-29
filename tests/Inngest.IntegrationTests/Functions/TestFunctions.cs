namespace Inngest.IntegrationTests.Functions;

// The functions of the app that the dev server tests run against.
internal static class TestFunctions
{
    public const string AppId = "itests";

    public static FunctionOptions Hello { get; } = new()
    {
        Id = "hello",
        Name = "Say hello",
        Triggers = [Trigger.Event("itests/hello")],
    };

    // What the server must list once the app is synced: "<app id>-<function id>", plus the companion function the SDK
    // registers for a failure handler.
    public static IReadOnlyList<string> Slugs { get; } =
    [
        "itests-hello",
        "itests-step-sequence",
        "itests-parallel",
        "itests-retry-function",
        "itests-retry-step",
        "itests-non-retriable",
        "itests-retry-after",
        "itests-sleep",
        "itests-sleep-until",
        "itests-wait-for-event",
        "itests-send-event",
        "itests-send-event-receiver",
        "itests-cancellable",
        "itests-invoke-parent",
        "itests-invoke-child",
        "itests-invoke-catches-failure",
        "itests-failing-child",
        "itests-always-fails",
        "itests-always-fails-failure",
        "itests-configured",
        "itests-batched",
        "itests-typed-order",
        "itests-typed-pricing",
        "itests-typed-confirmation",
    ];

    public static IInngestBuilder AddTestFunctions(this IInngestBuilder inngest)
    {
        // A delegate, to cover that way of registering next to the class-based functions.
        inngest.AddFunction<GreetEvent>(
            Hello,
            static (context, _) => Task.FromResult<object?>(new Greeting($"Hello, {context.Event.Data.Name}!")));

        inngest.AddFunction<StepSequenceFunction>(StepSequenceFunction.Options);
        inngest.AddFunction<ParallelFunction>(ParallelFunction.Options);
        inngest.AddFunction<FunctionRetryFunction>(FunctionRetryFunction.Options);
        inngest.AddFunction<StepRetryFunction>(StepRetryFunction.Options);
        inngest.AddFunction<NonRetriableFunction>(NonRetriableFunction.Options);
        inngest.AddFunction<RetryAfterFunction>(RetryAfterFunction.Options);
        inngest.AddFunction<SleepFunction>(SleepFunction.Options);
        inngest.AddFunction<SleepUntilFunction>(SleepUntilFunction.Options);
        inngest.AddFunction<WaitForEventFunction>(WaitForEventFunction.Options);
        inngest.AddFunction<SendEventFunction>(SendEventFunction.Options);
        inngest.AddFunction<SentEventReceiverFunction>(SentEventReceiverFunction.Options);
        inngest.AddFunction<CancellableFunction>(CancellableFunction.Options);
        inngest.AddFunction<InvokeParentFunction>(InvokeParentFunction.Options);
        inngest.AddFunction<InvokeChildFunction>(InvokeChildFunction.Options);
        inngest.AddFunction<InvokeCatchesFailureFunction>(InvokeCatchesFailureFunction.Options);
        inngest.AddFunction<FailingChildFunction>(FailingChildFunction.Options);
        inngest.AddFunction<AlwaysFailsFunction>(AlwaysFailsFunction.Options);
        inngest.AddFunction<ConfiguredFunction>(ConfiguredFunction.Options);
        inngest.AddFunction<BatchedFunction>(BatchedFunction.Options);

        // Configured by their attributes.
        inngest.AddFunction<TypedOrderFunction>();
        inngest.AddFunction<TypedPricingFunction>();
        inngest.AddFunction<TypedConfirmationFunction>();
        return inngest;
    }
}
