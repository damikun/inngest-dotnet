using System.Text;
using Inngest.IntegrationTests.Harness;
using Inngest.Testing.Containers;

namespace Inngest.IntegrationTests.Containers;

// An Inngest server from the Testcontainers module and an app that it has synced. The server starts first, the app next,
// and the container then syncs the app at the address the app listens on, so neither needs the other's address up front.
public abstract class ContainerFixture : IAsyncLifetime
{
    public InngestServerContainer Server { get; private set; } = null!;

    public ContainerApp App { get; private set; } = null!;

    public DevServerClient Api { get; private set; } = null!;

    protected abstract InngestServerBuilder Configure(InngestServerBuilder builder);

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Server = Configure(new InngestServerBuilder(InngestServer.Image)).Build();
        try
        {
            await Server.StartAsync(cancellationToken);
            App = await ContainerApp.StartAsync(Server.Configure, settings: null, cancellationToken);
            await Server.SyncAsync(App.Url, cancellationToken);

            // The dev server takes any event key. The self-hosted server wants its signing key on its REST API.
            Api = new DevServerClient(Server.GetBaseAddress(), Server.EventKey ?? "test", Server.SigningKey, DescribeAsync);
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task<string> DescribeAsync(CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        if (App is not null)
        {
            text.Append(App.Describe());
        }

        text.AppendLine("--- Inngest server log tail ---");
        var (stdout, stderr) = await Server.GetLogsAsync(timestampsEnabled: false, ct: cancellationToken);
        foreach (var line in (stdout + stderr).Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(30))
        {
            text.AppendLine(line);
        }

        return text.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        // The server goes first, so it stops calling an app that is shutting down.
        if (Server is not null)
        {
            await Server.DisposeAsync();
        }

        if (App is not null)
        {
            await App.DisposeAsync();
        }

        Api?.Dispose();
        GC.SuppressFinalize(this);
    }
}

// The dev server: it signs nothing and takes any event key.
public sealed class DevContainerFixture : ContainerFixture
{
    protected override InngestServerBuilder Configure(InngestServerBuilder builder) => builder;
}

// The self-hosted server, with keys the builder generates: it signs every request to the app and wants its signing key on
// the REST API, so the app runs in cloud mode with the same keys.
public sealed class SelfHostedContainerFixture : ContainerFixture
{
    protected override InngestServerBuilder Configure(InngestServerBuilder builder) => builder.WithSelfHosting();
}
