using Aspire.Hosting.Testing;

namespace Inngest.Aspire.Hosting.Tests.Containers;

// An AppHost that is started once for the tests that share it: the orchestrator runs the Inngest server in Docker and
// the test app as a process, exactly as a developer's AppHost does. Nothing in it uses a fixed port.
public abstract class AppHostFixture : IAsyncLifetime
{
    // Docker may have to pull the image, and the machine runs other builds.
    public static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(3);

    private IDistributedApplicationTestingBuilder? _builder;

    // A resource inherits the environment of the AppHost. These stand for what a developer may have left in a shell or a
    // launch profile, and an address nothing listens on: the settings that WithReference gives must win over every one.
    // They are set for the whole test process, whichever fixture starts first.
    static AppHostFixture()
    {
        Environment.SetEnvironmentVariable("INNGEST_DEV", "1");
        Environment.SetEnvironmentVariable("INNGEST_BASE_URL", "http://127.0.0.1:9");
        Environment.SetEnvironmentVariable("INNGEST_API_BASE_URL", "http://127.0.0.1:9");
        Environment.SetEnvironmentVariable("INNGEST_EVENT_API_BASE_URL", "http://127.0.0.1:9");
        Environment.SetEnvironmentVariable("INNGEST_CONNECT_GATEWAY_URL", "ws://127.0.0.1:9/v0/connect");
    }

    public DistributedApplication App { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        _builder = DistributedApplicationTestingBuilder.Create();
        Configure(_builder);
        App = await _builder.BuildAsync(TestContext.Current.CancellationToken);
        await App.StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (App is not null)
        {
            await App.StopAsync();
            await App.DisposeAsync();
        }

        if (_builder is not null)
        {
            await _builder.DisposeAsync();
        }
    }

    public Task WaitForHealthyAsync(string resourceName, CancellationToken cancellationToken) =>
        WaitForHealthyAsync(App, resourceName, cancellationToken);

    // A resource that failed to start, such as a server that refused its keys, ends the wait with the reason instead of
    // leaving the test to wait out the time limit.
    public static async Task WaitForHealthyAsync(DistributedApplication app, string resourceName, CancellationToken cancellationToken) =>
        await app.ResourceNotifications
            .WaitForResourceHealthyAsync(resourceName, WaitBehavior.StopOnResourceUnavailable, cancellationToken)
            .WaitAsync(StartTimeout, cancellationToken);

    protected abstract void Configure(IDistributedApplicationTestingBuilder builder);
}
