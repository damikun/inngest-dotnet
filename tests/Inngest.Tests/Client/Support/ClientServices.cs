using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Inngest.Tests.Client;

internal static class ClientServices
{
    // The container an app gets from AddInngest, with the HTTP traffic and the clock replaced. The variables
    // stand in for the process environment, which stays out of it. Without configureOptions the app is in dev mode.
    public static ServiceProvider Build(
        FakeHttpHandler handler,
        FakeTimeProvider time,
        Action<IInngestBuilder>? configureInngest = null,
        Action<IServiceCollection>? configureServices = null,
        IReadOnlyDictionary<string, string?>? variables = null,
        Action<InngestOptions>? configureOptions = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(variables ?? new Dictionary<string, string?>()).Build());
        services.AddSingleton<TimeProvider>(time);
        var builder = services.AddInngest(options =>
        {
            if (configureOptions is null)
            {
                options.AppId = "test-app";
                options.IsDev = true;
            }
            else
            {
                configureOptions(options);
            }
        });
        services.AddHttpClient("Inngest").ConfigurePrimaryHttpMessageHandler(() => handler);
        configureInngest?.Invoke(builder);
        configureServices?.Invoke(services);
        return services.BuildServiceProvider();
    }
}
