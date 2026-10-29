using Inngest.Internal;
using Inngest.Testing;
using Inngest.Tests.Internal;
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
}
