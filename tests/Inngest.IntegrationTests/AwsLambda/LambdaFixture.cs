using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.AwsLambda;

// A dev server and an app that is served the way a Lambda function is, behind a fake API Gateway.
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

    protected override void AddFunctions(IInngestBuilder inngest) => LambdaApp.Add(inngest, Gateway);
}
