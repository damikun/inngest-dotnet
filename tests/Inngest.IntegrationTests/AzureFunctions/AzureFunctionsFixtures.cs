using System.Security.Cryptography;
using Inngest.IntegrationTests.Harness;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.AzureFunctions;

// A self-hosted Inngest server that signs every request with the app's signing key, and the app in cloud mode behind a
// stand-in for the Functions host with the built-in HTTP model. The server syncs the app and calls its functions through
// the function that serves Inngest, so the signatures are checked over the bytes the worker hands that function.
public sealed class AzureFunctionsFixture : InngestFixture
{
    // inngest start only accepts plain hex of even length as the signing key.
    public string SigningKey { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public string ServerEventKey { get; } = $"azfn-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}";

    public override string AppId => AzureFunctionsFunctions.AppId;

    public override IReadOnlyCollection<string> ExpectedFunctions => [$"{AppId}-greet"];

    protected override string EventKey => ServerEventKey;

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

    protected override void AddFunctions(IInngestBuilder inngest)
    {
        inngest.Services.AddSingleton<IStartupFilter>(new FunctionsHostFilter(FunctionsHttpModel.BuiltIn));
        AzureFunctionsFunctions.AddGreeting(inngest);
    }
}

// The dev server and the app behind a stand-in for the Functions host with the ASP.NET Core integration, where the response
// of a function can be streamed.
public sealed class AzureFunctionsStreamingFixture : InngestFixture
{
    public override string AppId => AzureFunctionsFunctions.AppId;

    public override IReadOnlyCollection<string> ExpectedFunctions => [$"{AppId}-greet", $"{AppId}-slow"];

    // The dev server accepts any non-empty event key.
    protected override string EventKey => "azfn";

    protected override InngestServer CreateServer(int hostPort, Uri appUrl) => InngestServer.Dev(hostPort, appUrl);

    protected override void ConfigureInngest(InngestOptions options, Uri serverUrl)
    {
        options.AppId = AppId;
        options.IsDev = true;
        options.DevServerUrl = serverUrl;
        options.Streaming = true;
    }

    protected override void AddFunctions(IInngestBuilder inngest)
    {
        inngest.Services.AddSingleton<IStartupFilter>(new FunctionsHostFilter(FunctionsHttpModel.AspNetCore));
        AzureFunctionsFunctions.AddGreeting(inngest);
        AzureFunctionsFunctions.AddSlow(inngest);
    }
}

[CollectionDefinition(Name)]
public sealed class AzureFunctionsCollection : ICollectionFixture<AzureFunctionsFixture>
{
    public const string Name = "Azure Functions";
}

[CollectionDefinition(Name)]
public sealed class AzureFunctionsStreamingCollection : ICollectionFixture<AzureFunctionsStreamingFixture>
{
    public const string Name = "Azure Functions streaming";
}
