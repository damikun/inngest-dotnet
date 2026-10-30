using Inngest.Tests.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Inngest.Tests.AspNetCore;

// An app with AddInngest and the health checks of the test, whose requests to Inngest are scripted on Handler and whose
// clock is Time. The keys are made-up values that no other text of a test contains, so that a test can look for them.
internal sealed class HealthHarness : IAsyncDisposable
{
    public const string SigningKey = "signkey-prod-0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    public const string FallbackSigningKey = "signkey-prod-fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";
    public const string EventKey = "evtkey-sentinel-4c1f9e";

    public static readonly DateTimeOffset Start = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private HealthHarness(Action<IHealthChecksBuilder> addChecks, Action<InngestOptions> configure, bool systemTime, int functions) =>
        Services = ClientServices.Build(
            Handler,
            Time,
            configureInngest: inngest =>
            {
                for (var i = 1; i <= functions; i++)
                {
                    inngest.AddFunction(
                        new FunctionOptions { Id = $"f{i}", Triggers = [Trigger.Event($"tests/f{i}")] },
                        static (_, _) => Task.FromResult<object?>(null));
                }
            },
            configureServices: services =>
            {
                services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddFakeLogging());
                if (systemTime)
                {
                    services.Replace(ServiceDescriptor.Singleton<TimeProvider>(TimeProvider.System));
                }

                addChecks(services.AddHealthChecks());
            },
            configureOptions: configure);

    public FakeHttpHandler Handler { get; } = new();

    public FakeTimeProvider Time { get; } = new(Start);

    public ServiceProvider Services { get; }

    public FakeLogCollector Logs => Services.GetFakeLogCollector();

    // Cloud mode with a signing key, the way an app runs in production. configure can take the key away or add others,
    // and functions is the number of functions the app serves.
    public static HealthHarness Cloud(Action<IHealthChecksBuilder>? addChecks = null, Action<InngestOptions>? configure = null, bool systemTime = false, int functions = 0) =>
        new(
            addChecks ?? (static checks => checks.AddInngest()),
            options =>
            {
                options.AppId = "test-app";
                options.IsDev = false;
                options.SigningKey = SigningKey;
                configure?.Invoke(options);
            },
            systemTime,
            functions);

    public static HealthHarness Dev(Action<IHealthChecksBuilder>? addChecks = null, Action<InngestOptions>? configure = null, int functions = 0) =>
        new(
            addChecks ?? (static checks => checks.AddInngest()),
            options =>
            {
                options.AppId = "test-app";
                options.IsDev = true;
                configure?.Invoke(options);
            },
            systemTime: false,
            functions);

    // Runs every registered check once, as a probe would, and returns the entry of one.
    public async Task<HealthReportEntry> CheckAsync(string name = "inngest", CancellationToken cancellationToken = default)
    {
        var report = await Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(cancellationToken);
        return report.Entries[name];
    }

    public async Task WaitForRequestsAsync(int count)
    {
        using var giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (Handler.Requests.Count < count)
        {
            await Task.Delay(1, giveUp.Token);
        }
    }

    public ValueTask DisposeAsync() => Services.DisposeAsync();
}
