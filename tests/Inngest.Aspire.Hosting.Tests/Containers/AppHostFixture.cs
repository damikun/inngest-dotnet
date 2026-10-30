using Aspire.Hosting.Testing;

namespace Inngest.Aspire.Hosting.Tests.Containers;

// An AppHost that is started once for the tests that share it: the orchestrator runs the Inngest server in Docker and
// the test app as a process, exactly as a developer's AppHost does. Nothing in it uses a fixed port.
public abstract class AppHostFixture : IAsyncLifetime
{
    // Docker may have to pull the image, and the machine runs other builds.
    public static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(3);

    private IDistributedApplicationTestingBuilder? _builder;

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

    public async Task WaitForHealthyAsync(string resourceName, CancellationToken cancellationToken) =>
        await App.ResourceNotifications.WaitForResourceHealthyAsync(resourceName, cancellationToken).WaitAsync(StartTimeout, cancellationToken);

    protected abstract void Configure(IDistributedApplicationTestingBuilder builder);
}
