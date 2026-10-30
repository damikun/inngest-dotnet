using Inngest.IntegrationTests.Functions;
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

    // The fixture's app is synced with every test function, and this instance serves the same ones.
    [Fact(Timeout = 30_000)]
    public async Task Finds_the_app_synced_with_the_functions_this_instance_serves()
    {
        await using var app = HealthApp.Create(Configure(Fixture.AppId), checks => checks.AddInngest(options => options.CheckSync = true), inngest => inngest.AddTestFunctions());

        var entry = await app.CheckAsync(Cancellation);

        entry.Status.ShouldBe(HealthStatus.Healthy);
        entry.Description.ShouldBe($"Reached the Inngest dev server. The app \"{Fixture.AppId}\" is synced with {TestFunctions.Slugs.Count} functions.");
        entry.Data["sync"].ShouldBe("synced");
        entry.Data["functionsInInngest"].ShouldBe(TestFunctions.Slugs.Count);
        entry.Data["functionsServed"].ShouldBe(TestFunctions.Slugs.Count);
    }

    [Fact(Timeout = 30_000)]
    public async Task Fails_while_the_dev_server_has_no_app_by_that_id()
    {
        await using var app = HealthApp.Create(Configure("itests-never-synced"), checks => checks.AddInngest(options => options.CheckSync = true), inngest => inngest.AddTestFunctions());

        var entry = await app.CheckAsync(Cancellation);

        entry.Status.ShouldBe(HealthStatus.Unhealthy);
        entry.Description.ShouldBe("Inngest has no app \"itests-never-synced\". Sync the app so that Inngest knows its functions.");
        entry.Data["sync"].ShouldBe("not registered");
    }

    // A function added in code and not yet synced: the case that a forgotten sync after a deploy comes to.
    [Fact(Timeout = 30_000)]
    public async Task Fails_while_this_instance_serves_a_function_the_dev_server_was_not_told_of()
    {
        await using var app = HealthApp.Create(
            Configure(Fixture.AppId),
            checks => checks.AddInngest(options => options.CheckSync = true),
            inngest =>
            {
                inngest.AddTestFunctions();
                inngest.AddFunction(
                    new FunctionOptions { Id = "added-after-the-last-sync", Triggers = [Trigger.Event("itests/added")] },
                    static (_, _) => Task.FromResult<object?>(null));
            });

        var entry = await app.CheckAsync(Cancellation);

        entry.Status.ShouldBe(HealthStatus.Unhealthy);
        entry.Description.ShouldBe(
            $"Inngest has {TestFunctions.Slugs.Count} functions for the app \"{Fixture.AppId}\", and this instance serves {TestFunctions.Slugs.Count + 1}. Sync the app.");
        entry.Data["sync"].ShouldBe("function count mismatch");
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

    private Action<InngestOptions> Configure(string appId) =>
        options =>
        {
            options.AppId = appId;
            options.IsDev = true;
            options.DevServerUrl = Fixture.Server.Url;
        };
}
