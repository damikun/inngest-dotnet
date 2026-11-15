using Inngest.Configuration;
using Inngest.Testing;
using Inngest.Tests.Internal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using static Inngest.Tests.Testing.TestHosts;

namespace Inngest.Tests.Testing;

public sealed class TestHostConfigurationTests
{
    [Fact]
    public async Task The_host_runs_with_the_app_id_from_the_configuration_section()
    {
        var services = new ServiceCollection();
        services
            .AddInngest(InngestSection.Of(("AppId", "billing")))
            .AddFunction(Options(), (_, _) => Task.FromResult<object?>("done"));
        await using var host = new InngestTestHost(services);

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        run.GetOutput<string>().ShouldBe("done");
        host.Services.GetRequiredService<InngestSettings>().AppId.ShouldBe("billing");
    }

    // An app checks its settings with the INNGEST_* variables when it starts. The host leaves them out, so a variable
    // left set on a developer's machine or a build agent changes nothing about the tests.
    [Fact]
    public async Task The_host_starts_whatever_INNGEST_variables_are_set()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["INNGEST_EVENT_API_BASE_URL"] = "ftp://files.example.com" })
            .Build());
        services
            .AddInngest(options => options.AppId = "billing")
            .AddFunction(Options(), (_, _) => Task.FromResult<object?>("done"));
        await using var host = new InngestTestHost(services);

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
    }

    [Fact]
    public void A_setting_the_SDK_cannot_read_still_fails_the_host()
    {
        var services = new ServiceCollection();
        services
            .AddInngest(options =>
            {
                options.AppId = "billing";
                options.EventApiBaseUrl = new Uri("ftp://files.example.com");
            })
            .AddFunction(Options(), (_, _) => Task.FromResult<object?>("done"));

        var exception = Should.Throw<InvalidOperationException>(() => new InngestTestHost(services));

        exception.Message.ShouldBe("InngestOptions.EventApiBaseUrl must be an absolute http or https URL.");
    }
}
