// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Inngest;
using Inngest.AspNetCore;
using Inngest.Configuration;
using Inngest.Registration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Microsoft.Extensions.DependencyInjection;

public static class InngestHealthChecksBuilderExtensions
{
    private const string DefaultName = "inngest";

    /// <summary>
    /// Adds a check that an app serving functions with MapInngest can work with Inngest: its signing key is configured,
    /// and Inngest answers a request that carries the key. In dev mode the check is that the dev server answers.
    /// Inngest's answer is kept for <see cref="InngestHealthCheckOptions.CacheDuration" />, so frequent probes send few
    /// requests, and a request is cancelled with the probe, or when <paramref name="timeout" /> has passed (10 seconds
    /// unless given), whichever is first.
    /// </summary>
    /// <remarks>
    /// The check reports failureStatus (Unhealthy unless given) when it finds a problem, and Degraded when Inngest rate
    /// limits the request. Use Degraded as the failureStatus for an app that mostly serves its own users and only runs
    /// background work through Inngest, so that an outage of Inngest does not take it out of rotation. The check is meant
    /// for readiness probes: tag it and keep it out of the liveness probe, because restarting the app does not bring
    /// Inngest back. Unless <see cref="InngestHealthCheckOptions.CheckSync" /> is set, it does not tell whether Inngest
    /// has synced the app.
    /// </remarks>
    public static IHealthChecksBuilder AddInngest(
        this IHealthChecksBuilder builder,
        string? name = null,
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null) =>
        builder.AddInngest(static _ => { }, name, failureStatus, tags, timeout);

    /// <summary>
    /// Adds the check described at <see cref="AddInngest(IHealthChecksBuilder, string?, HealthStatus?, IEnumerable{string}?, TimeSpan?)" />,
    /// with the options set by <paramref name="configure" />.
    /// </summary>
    public static IHealthChecksBuilder AddInngest(
        this IHealthChecksBuilder builder,
        Action<InngestHealthCheckOptions> configure,
        string? name = null,
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        name ??= DefaultName;
        ArgumentException.ThrowIfNullOrEmpty(name);

        var options = new InngestHealthCheckOptions();
        configure(options);
        var registration = new Registration(InngestHealthPolicy.From(options));
        return builder.Add(new HealthCheckRegistration(name, registration.Create, failureStatus, tags, timeout));
    }

    // The health check service builds the check again for every run, in a scope of its own, while the probe holds what
    // must outlive a run. The probe is built on the first run, from services that live as long as the app, and not
    // from the scope that first asked.
    private sealed class Registration(InngestHealthPolicy policy)
    {
        private InngestHealthProbe? _probe;

        public IHealthCheck Create(IServiceProvider services)
        {
            // The health check service calls the factory outside the guard that turns an exception into a result, so a
            // missing AddInngest is reported by a check instead of thrown here.
            if (services.GetService<InngestSettings>() is not { } settings)
            {
                return new MissingInngestCheck();
            }

            var probe = _probe;
            if (probe is null)
            {
                probe = new InngestHealthProbe(
                    services.GetRequiredService<IHttpClientFactory>(),
                    settings,
                    policy.CheckSync ? services.GetRequiredService<IFunctionRegistry>() : null,
                    policy,
                    services.GetService<TimeProvider>() ?? TimeProvider.System,
                    services.GetService<ILogger<InngestHealthCheck>>() ?? NullLogger<InngestHealthCheck>.Instance);
                probe = Interlocked.CompareExchange(ref _probe, probe, null) ?? probe;
            }

            return new InngestHealthCheck(settings, policy, probe);
        }
    }

    private sealed class MissingInngestCheck : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HealthCheckResult(context.Registration.FailureStatus, "Inngest is not set up. Call services.AddInngest() before checking its health."));
    }
}
