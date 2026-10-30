using Inngest.IntegrationTests.Functions;
using Inngest.IntegrationTests.Harness;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.AwsLambda;

// A dev server and an app that is served the way a Lambda function is: the Inngest endpoint is not mapped, and a fake
// API Gateway in front of the app turns every request into a Lambda event for InngestLambdaHandler.
public sealed class LambdaFixture : InngestFixture
{
    public override string AppId => "lambda-itests";

    public override IReadOnlyCollection<string> ExpectedFunctions => [LambdaStepsFunction.Slug];

    // What the fake gateway turns requests into, which tests switch.
    public GatewaySettings Gateway { get; } = new();

    // The dev server accepts any non-empty event key.
    protected override string EventKey => "itests";

    protected override InngestServer CreateServer(int hostPort, Uri appUrl) => InngestServer.Dev(hostPort, appUrl);

    protected override void ConfigureInngest(InngestOptions options, Uri serverUrl)
    {
        options.AppId = AppId;
        options.IsDev = true;
        options.DevServerUrl = serverUrl;
    }

    protected override void AddFunctions(IInngestBuilder inngest)
    {
        inngest.AddFunction<LambdaStepsFunction>(LambdaStepsFunction.Options);
        inngest.Services.AddSingleton(Gateway);
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
