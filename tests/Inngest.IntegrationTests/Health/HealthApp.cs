using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Inngest.IntegrationTests.Health;

// The container of an app that has the SDK and the health check of the test, pointed at a real Inngest server. It
// serves nothing: the check only talks to Inngest, so no endpoint has to be up.
internal sealed class HealthApp : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private HealthApp(Action<InngestOptions> configure, Action<IInngestBuilder>? addFunctions, Action<IHealthChecksBuilder> addChecks)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // An empty configuration keeps INNGEST_* variables of the developer's machine out of the settings.
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        var inngest = services.AddInngest(configure);
        addFunctions?.Invoke(inngest);
        addChecks(services.AddHealthChecks());
        _services = services.BuildServiceProvider();
    }

    public static HealthApp Create(Action<InngestOptions> configure, Action<IHealthChecksBuilder> addChecks, Action<IInngestBuilder>? addFunctions = null) =>
        new(configure, addFunctions, addChecks);

    public async Task<HealthReportEntry> CheckAsync(CancellationToken cancellationToken, string name = "inngest")
    {
        var report = await _services.GetRequiredService<HealthCheckService>().CheckHealthAsync(cancellationToken);
        return report.Entries[name];
    }

    public ValueTask DisposeAsync() => _services.DisposeAsync();
}
