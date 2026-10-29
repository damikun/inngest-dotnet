using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.Functions;

// The functions of the app that runs in cloud mode against a self-hosted server with a signing key.
internal static class SigningFunctions
{
    public static IReadOnlyList<string> Slugs { get; } = ["itests-signed-hello", "itests-signed-steps"];

    public static IInngestBuilder AddSigningFunctions(this IInngestBuilder inngest)
    {
        inngest.AddFunction<SignedHelloFunction>(SignedHelloFunction.Options);
        inngest.AddFunction<SignedStepsFunction>(SignedStepsFunction.Options);
        return inngest;
    }
}

// No steps: one call, one response. Tests also call it directly to check how the app treats signatures.
internal sealed class SignedHelloFunction(Probe probe) : InngestFunction<GreetEvent>
{
    public static FunctionOptions Options { get; } = new()
    {
        Id = "signed-hello",
        Triggers = [Trigger.Event("itests/signed.hello")],
    };

    public override Task<object?> ExecuteAsync(FunctionContext<GreetEvent> context, CancellationToken cancellationToken)
    {
        probe.For(context.Event.Data.TestId).Record("ran");
        return Task.FromResult<object?>(new Greeting($"Hello, {context.Event.Data.Name}!"));
    }
}

// Several signed calls and responses in one run.
internal sealed class SignedStepsFunction(Probe probe) : InngestFunction<NumberEvent>
{
    public static FunctionOptions Options { get; } = new()
    {
        Id = "signed-steps",
        Triggers = [Trigger.Event("itests/signed.steps")],
    };

    public override async Task<object?> ExecuteAsync(FunctionContext<NumberEvent> context, CancellationToken cancellationToken)
    {
        var observed = probe.For(context.Event.Data.TestId);
        var first = await context.Step.RunAsync("first", () =>
        {
            observed.Record("first");
            return context.Event.Data.Value + 1;
        });
        var second = await context.Step.RunAsync("second", () =>
        {
            observed.Record("second");
            return first * 10;
        });

        return new SequenceResult(first, second, $"{first}:{second}");
    }
}
