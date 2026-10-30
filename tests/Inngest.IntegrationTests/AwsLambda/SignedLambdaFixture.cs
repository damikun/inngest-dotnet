using System.Security.Cryptography;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.AwsLambda;

// A self-hosted server that signs every request with a signing key, and the same app in cloud mode with that key, so
// that the signatures are checked and made by the real thing.
public sealed class SignedLambdaFixture : InngestFixture
{
    // inngest start only accepts plain hex of even length as the signing key.
    public string SigningKey { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public string ServerEventKey { get; } = $"itests-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}";

    public override string AppId => "lambda-itests";

    public override IReadOnlyCollection<string> ExpectedFunctions => [LambdaStepsFunction.Slug];

    public GatewaySettings Gateway { get; } = new();

    protected override string EventKey => ServerEventKey;

    // The server accepts the plain key as well as its hashed form.
    protected override string? ApiKey => SigningKey;

    protected override InngestServer CreateServer(int hostPort, Uri appUrl) =>
        InngestServer.SelfHosted(hostPort, appUrl, SigningKey, ServerEventKey);

    protected override void ConfigureInngest(InngestOptions options, Uri serverUrl)
    {
        options.AppId = AppId;
        options.IsDev = false;
        options.SigningKey = SigningKey;
        options.EventKey = ServerEventKey;
        options.BaseUrl = serverUrl;
    }

    protected override void AddFunctions(IInngestBuilder inngest) => LambdaApp.Add(inngest, Gateway);
}
