using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.UserContext;

// The dev server and an app that restores users. Its Data Protection key ring is a folder, which the web apps that
// the tests start share, as two deployments of one system would share a store.
public sealed class UserContextFixture : InngestFixture
{
    public static DirectoryInfo Keys { get; } = CreateKeyFolder();

    public override string AppId => UserContextFunctions.AppId;

    public override IReadOnlyCollection<string> ExpectedFunctions => [.. UserContextFunctions.Ids.Select(id => $"{AppId}-{id}")];

    // The dev server accepts any non-empty event key.
    protected override string EventKey => "itests";

    protected override InngestServer CreateServer(int hostPort, Uri appUrl) => InngestServer.Dev(hostPort, appUrl);

    protected override void ConfigureInngest(InngestOptions options, Uri serverUrl)
    {
        options.AppId = AppId;
        options.IsDev = true;
        options.DevServerUrl = serverUrl;
    }

    protected override void AddFunctions(IInngestBuilder inngest) => UserContextFunctions.Add(inngest, Keys);

    private static DirectoryInfo CreateKeyFolder()
    {
        var folder = Directory.CreateTempSubdirectory("inngest-user-context-itests-");
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                folder.Delete(recursive: true);
            }
            catch (IOException)
            {
                // Left behind in the temp folder, which the system cleans up.
            }
        };

        return folder;
    }
}
