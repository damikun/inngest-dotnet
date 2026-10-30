using Inngest.AspNetCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Inngest.Tests.AspNetCore;

// AddInngest registers like the other health checks do: a name, a failure status, tags and a timeout, and options for
// what is specific to the check.
public sealed class InngestHealthChecksRegistrationTests
{
    [Fact]
    public async Task Registers_with_a_name_and_the_defaults_of_the_health_checks()
    {
        await using var app = HealthHarness.Cloud();

        var registration = RegistrationsOf(app).Single();

        registration.Name.ShouldBe("inngest");
        registration.FailureStatus.ShouldBe(HealthStatus.Unhealthy);
        registration.Tags.ShouldBeEmpty();
        registration.Timeout.ShouldBe(Timeout.InfiniteTimeSpan);
        registration.Factory(app.Services).ShouldBeOfType<InngestHealthCheck>();
    }

    [Fact]
    public async Task Registers_with_the_name_failure_status_tags_and_timeout_it_is_given()
    {
        await using var app = HealthHarness.Cloud(
            checks => checks.AddInngest("inngest-ready", HealthStatus.Degraded, ["ready", "inngest"], TimeSpan.FromSeconds(3)));

        var registration = RegistrationsOf(app).Single();

        registration.Name.ShouldBe("inngest-ready");
        registration.FailureStatus.ShouldBe(HealthStatus.Degraded);
        registration.Tags.ShouldBe(["ready", "inngest"], ignoreOrder: true);
        registration.Timeout.ShouldBe(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task The_overload_with_options_takes_the_same_arguments()
    {
        await using var app = HealthHarness.Cloud(
            checks => checks.AddInngest(static options => options.RequireEventKey = true, "inngest-ready", HealthStatus.Degraded, ["ready"], TimeSpan.FromSeconds(3)));

        var registration = RegistrationsOf(app).Single();

        registration.Name.ShouldBe("inngest-ready");
        registration.FailureStatus.ShouldBe(HealthStatus.Degraded);
        registration.Tags.ShouldBe(["ready"]);
        registration.Timeout.ShouldBe(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void The_options_start_with_no_event_key_required_and_a_cache_that_a_failure_leaves_early()
    {
        var options = new InngestHealthCheckOptions();

        options.RequireEventKey.ShouldBeFalse();
        options.CacheDuration.ShouldBe(TimeSpan.FromSeconds(30));
        options.FailureCacheDuration.ShouldBe(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Is_not_changed_by_options_that_are_changed_after_registering()
    {
        InngestHealthCheckOptions? captured = null;
        await using var app = HealthHarness.Cloud(checks => checks.AddInngest(options => captured = options));
        app.Handler.RespondAlways(System.Net.HttpStatusCode.OK);

        captured.ShouldNotBeNull().RequireEventKey = true;
        var entry = await app.CheckAsync(cancellationToken: TestContext.Current.CancellationToken);

        entry.Status.ShouldBe(HealthStatus.Healthy);
    }

    [Theory]
    [InlineData(-1, 5)]
    [InlineData(30, -1)]
    public void Rejects_a_negative_cache_duration(int cacheSeconds, int failureSeconds)
    {
        var checks = new ServiceCollection().AddHealthChecks();

        Should.Throw<InvalidOperationException>(() => checks.AddInngest(options =>
        {
            options.CacheDuration = TimeSpan.FromSeconds(cacheSeconds);
            options.FailureCacheDuration = TimeSpan.FromSeconds(failureSeconds);
        })).Message.ShouldContain("cannot be negative");
    }

    [Fact]
    public void Rejects_arguments_that_cannot_work()
    {
        var checks = new ServiceCollection().AddHealthChecks();

        Should.Throw<ArgumentNullException>(() => InngestHealthChecksBuilderExtensions.AddInngest(null!));
        Should.Throw<ArgumentNullException>(() => checks.AddInngest((Action<InngestHealthCheckOptions>)null!));
        Should.Throw<ArgumentException>(() => checks.AddInngest(name: string.Empty));
    }

    private static ICollection<HealthCheckRegistration> RegistrationsOf(HealthHarness app) =>
        app.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
}
