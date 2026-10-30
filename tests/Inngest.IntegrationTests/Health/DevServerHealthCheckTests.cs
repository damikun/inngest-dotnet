using Inngest.IntegrationTests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Inngest.IntegrationTests.Health;

// The health check of an app in dev mode, against the dev server that the whole assembly shares.
public sealed class DevServerHealthCheckTests(DevServerFixture fixture) : IntegrationTest<DevServerFixture>(fixture)
{
    [Fact(Timeout = 30_000)]
    public async Task Is_healthy_while_the_dev_server_is_up()
    {
        await using var app = HealthApp.Create(
            options =>
            {
                options.AppId = Fixture.AppId;
                options.IsDev = true;
                options.DevServerUrl = Fixture.Server.Url;
            },
            checks => checks.AddInngest(tags: ["ready"]));

        var entry = await app.CheckAsync(Cancellation);

        entry.Status.ShouldBe(HealthStatus.Healthy);
        entry.Description.ShouldBe("Reached the Inngest dev server.");
        entry.Tags.ShouldBe(["ready"]);
        entry.Data["mode"].ShouldBe("dev");
        entry.Data["apiBaseUrl"].ShouldBe($"http://localhost:{Fixture.Server.Url.Port}");
        entry.Data["api"].ShouldBe("ok");
        entry.Data["apiStatusCode"].ShouldBe(200);
    }

    [Fact(Timeout = 30_000)]
    public async Task Is_unhealthy_when_nothing_listens_where_the_dev_server_should_be()
    {
        var closedPort = FreePorts.Pick(1)[0];
        await using var app = HealthApp.Create(
            options =>
            {
                options.AppId = Fixture.AppId;
                options.IsDev = true;
                options.DevServerUrl = new Uri($"http://localhost:{closedPort}");
            },
            checks => checks.AddInngest(failureStatus: HealthStatus.Degraded));

        var entry = await app.CheckAsync(Cancellation);

        entry.Status.ShouldBe(HealthStatus.Degraded);
        entry.Description.ShouldNotBeNull().ShouldStartWith($"Could not reach Inngest at http://localhost:{closedPort}:");
        entry.Data["api"].ShouldBe("unreachable");
    }
}
