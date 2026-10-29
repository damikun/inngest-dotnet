using Inngest.IntegrationTests.Functions;

namespace Inngest.IntegrationTests.Harness;

// The dev server and an app in dev mode with every test function, shared by the whole assembly. Tests isolate
// themselves through the test id in their events, so test classes run in parallel against it.
public sealed class DevServerFixture : InngestFixture
{
    public override string AppId => TestFunctions.AppId;

    public override IReadOnlyCollection<string> ExpectedFunctions => [.. TestFunctions.Slugs];

    // The dev server accepts any non-empty event key.
    protected override string EventKey => "itests";

    protected override InngestServer CreateServer(int hostPort, Uri appUrl) => InngestServer.Dev(hostPort, appUrl);

    protected override void ConfigureInngest(InngestOptions options, Uri serverUrl)
    {
        options.AppId = AppId;
        options.IsDev = true;
        options.DevServerUrl = serverUrl;
    }

    protected override void AddFunctions(IInngestBuilder inngest) => inngest.AddTestFunctions();
}
