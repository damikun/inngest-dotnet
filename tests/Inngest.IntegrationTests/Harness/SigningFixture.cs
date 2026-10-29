using System.Security.Cryptography;
using Inngest.IntegrationTests.Functions;

namespace Inngest.IntegrationTests.Harness;

// A self-hosted server (inngest start) with a signing key and an event key, and an app in cloud mode configured with
// the same keys. The server signs every request to the app and requires the key on its API, so this exercises
// signature checks, response signing and the SDK's bearer token for real.
public sealed class SigningFixture : InngestFixture
{
    // inngest start only accepts plain hex of even length as the signing key.
    public string SigningKey { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public string ServerEventKey { get; } = $"itests-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}";

    public override string AppId => TestFunctions.AppId;

    public override IReadOnlyCollection<string> ExpectedFunctions => [.. SigningFunctions.Slugs];

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

    protected override void AddFunctions(IInngestBuilder inngest) => inngest.AddSigningFunctions();
}

// The signing tests and the checks of the signing fixture itself share one server.
[CollectionDefinition(Name)]
public sealed class SigningCollection : ICollectionFixture<SigningFixture>
{
    public const string Name = "Signing";
}
