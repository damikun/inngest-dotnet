// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

namespace Inngest.AspNetCore;

// The options as they were when the check was registered, checked once, so that code holding the options object
// cannot change a registered check afterwards.
internal readonly record struct InngestHealthPolicy(bool RequireEventKey, TimeSpan CacheDuration, TimeSpan FailureCacheDuration)
{
    public static InngestHealthPolicy From(InngestHealthCheckOptions options)
    {
        if (options.CacheDuration < TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{nameof(InngestHealthCheckOptions)}.{nameof(InngestHealthCheckOptions.CacheDuration)} cannot be negative.");
        }

        if (options.FailureCacheDuration < TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{nameof(InngestHealthCheckOptions)}.{nameof(InngestHealthCheckOptions.FailureCacheDuration)} cannot be negative.");
        }

        return new InngestHealthPolicy(options.RequireEventKey, options.CacheDuration, options.FailureCacheDuration);
    }

    // How long a result is reused. A failure is retried sooner than a success is renewed, except a rate limit, which
    // asks for fewer requests rather than more.
    public TimeSpan LifetimeOf(ProbeResult result) =>
        result.Api is ApiStatus.Ok or ApiStatus.RateLimited
            ? CacheDuration
            : TimeSpan.FromTicks(Math.Min(FailureCacheDuration.Ticks, CacheDuration.Ticks));
}
