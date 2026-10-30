using Inngest.IntegrationTests.Functions;
using Inngest.IntegrationTests.Harness;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.AwsLambda;

// The app both fixtures run: a function, the Lambda handler, and the fake API Gateway in front of the app that turns every
// request into a Lambda event for it. The Inngest endpoint is not mapped.
internal static class LambdaApp
{
    public static void Add(IInngestBuilder inngest, GatewaySettings gateway)
    {
        inngest.AddFunction<LambdaStepsFunction>(LambdaStepsFunction.Options);
        inngest.Services.AddSingleton(gateway);
        inngest.Services.AddSingleton(services => new InngestLambdaHandler(services));
        inngest.Services.AddSingleton<IStartupFilter, FakeApiGateway>();
    }
}

// Steps and a sleep, so that a run takes several calls: the first, one for each step and one that returns the output.
internal sealed class LambdaStepsFunction(Probe probe) : InngestFunction<NumberEvent>
{
    public const string EventName = "itests/lambda.steps";
    public const string Slug = "lambda-itests-steps";

    public static FunctionOptions Options { get; } = new()
    {
        Id = "steps",
        Triggers = [Trigger.Event(EventName)],
    };

    public override async Task<object?> ExecuteAsync(FunctionContext<NumberEvent> context, CancellationToken cancellationToken)
    {
        var observed = probe.For(context.Event.Data.TestId);
        var first = await context.Step.RunAsync("first", () =>
        {
            observed.Record("first");
            return context.Event.Data.Value + 1;
        });
        await context.Step.SleepAsync("pause", TimeSpan.FromSeconds(1));
        var second = await context.Step.RunAsync("second", () =>
        {
            observed.Record("second");
            return first * 10;
        });

        return new SequenceResult(first, second, $"{first}:{second}");
    }
}
