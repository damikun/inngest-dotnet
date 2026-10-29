using Inngest.Connect;
using Inngest.Tests.Connect.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Inngest.Tests.Connect;

// A readiness check: healthy only while the worker holds an active connection, the registration's failure status in
// every other state.
public sealed class ConnectHealthCheckTests
{
    [Theory]
    [InlineData(InngestConnectionState.Connecting)]
    [InlineData(InngestConnectionState.Reconnecting)]
    [InlineData(InngestConnectionState.Closing)]
    [InlineData(InngestConnectionState.Closed)]
    public async Task Reports_the_failure_status_unless_the_connection_is_active(InngestConnectionState state)
    {
        var check = new ConnectHealthCheck(new FixedConnection(state));

        var result = await check.CheckHealthAsync(ContextFor(check, HealthStatus.Degraded), TestContext.Current.CancellationToken);

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Description.ShouldNotBeNullOrEmpty();
        result.Data["state"].ShouldBe(state.ToString());
    }

    [Fact]
    public async Task Is_healthy_while_the_connection_is_active()
    {
        var check = new ConnectHealthCheck(new FixedConnection(InngestConnectionState.Active));

        var result = await check.CheckHealthAsync(ContextFor(check, HealthStatus.Unhealthy), TestContext.Current.CancellationToken);

        result.Status.ShouldBe(HealthStatus.Healthy);
        result.Data["state"].ShouldBe("Active");
    }

    [Fact]
    public async Task Registers_on_the_health_checks_builder_with_its_name_failure_status_and_tags()
    {
        var connection = new InngestConnection();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IInngestConnection>(connection);
        services.AddHealthChecks().AddInngestConnect(tags: ["ready"]);
        await using var provider = services.BuildServiceProvider();
        var health = provider.GetRequiredService<HealthCheckService>();

        var connecting = (await health.CheckHealthAsync(TestContext.Current.CancellationToken)).Entries["inngest-connect"];
        connection.MoveTo(InngestConnectionState.Active);
        var active = (await health.CheckHealthAsync(TestContext.Current.CancellationToken)).Entries["inngest-connect"];

        connecting.Status.ShouldBe(HealthStatus.Unhealthy);
        connecting.Tags.ShouldBe(["ready"]);
        active.Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public async Task Follows_the_worker_from_connecting_to_closed()
    {
        await using var gateway = await FakeGateway.StartAsync();
        var worker = new WorkerHost(gateway);
        await using (worker)
        {
            var connection = worker.Services.GetRequiredService<IInngestConnection>();
            var check = new ConnectHealthCheck(connection);
            var context = ContextFor(check, HealthStatus.Unhealthy);
            (await check.CheckHealthAsync(context, TestContext.Current.CancellationToken)).Status.ShouldBe(HealthStatus.Unhealthy);

            await worker.StartAsync();
            await gateway.NextReadySessionAsync();
            await Poll.UntilAsync(() => connection.State == InngestConnectionState.Active);
            (await check.CheckHealthAsync(context, TestContext.Current.CancellationToken)).Status.ShouldBe(HealthStatus.Healthy);

            await worker.StopAsync(TestContext.Current.CancellationToken).WaitAsync(Waits.Timeout, TestContext.Current.CancellationToken);
            var stopped = await check.CheckHealthAsync(context, TestContext.Current.CancellationToken);
            stopped.Status.ShouldBe(HealthStatus.Unhealthy);
            stopped.Data["state"].ShouldBe("Closed");
        }
    }

    private static HealthCheckContext ContextFor(IHealthCheck check, HealthStatus failureStatus) =>
        new() { Registration = new HealthCheckRegistration("inngest-connect", check, failureStatus, tags: null) };

    private sealed class FixedConnection(InngestConnectionState state) : IInngestConnection
    {
        public InngestConnectionState State => state;

        public Task<InngestConnectionState> WaitForStateChangedAsync(InngestConnectionState lastObservedState, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
