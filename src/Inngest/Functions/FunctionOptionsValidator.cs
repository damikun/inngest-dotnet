// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Globalization;
using Inngest.Internal;

namespace Inngest.Functions;

// Applies the limits Inngest enforces on a function, so a mistake fails at startup and names the function
// instead of failing a sync with an error about the whole app.
internal static class FunctionOptionsValidator
{
    private const int MaxRetries = 20;
    private const int MaxTriggers = 10;
    private const int MaxConcurrencyLimits = 2;
    private const int MaxCancellations = 5;
    private const int MinBatchSize = 2;

    private static readonly TimeSpan MinPeriod = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxDebouncePeriod = TimeSpan.FromDays(7);
    private static readonly TimeSpan MaxRateLimitPeriod = TimeSpan.FromHours(24);
    private static readonly TimeSpan MaxCronJitter = TimeSpan.FromMinutes(5);

    public static void Validate(FunctionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Id))
        {
            var which = string.IsNullOrWhiteSpace(options.Name) ? "A function" : $"The function named \"{options.Name}\"";
            throw new InvalidOperationException($"{which} has no Id. Set FunctionOptions.Id to an id that is unique in the app and never changes.");
        }

        if (options.Retries is < 0 or > MaxRetries)
        {
            throw Invalid(options, $"Retries must be between 0 and {MaxRetries}, but was {options.Retries}.");
        }

        ValidateTriggers(options);
        ValidateConcurrency(options);
        ValidateThrottle(options);
        ValidateRateLimit(options);
        ValidateDebounce(options);
        ValidateBatchEvents(options);
        ValidatePriority(options);
        ValidateCancelOn(options);
        ValidateTimeouts(options);
        ValidateSingleton(options);
        ValidateMiddleware(options);
        ValidateCheckpointing(options);
    }

    // An empty trigger list is valid: the function then runs only when another function invokes it.
    private static void ValidateTriggers(FunctionOptions options)
    {
        if (options.Triggers.Count > MaxTriggers)
        {
            throw Invalid(options, $"A function can have at most {MaxTriggers} triggers, but has {options.Triggers.Count}.");
        }

        foreach (var trigger in options.Triggers)
        {
            switch (trigger)
            {
                case EventTrigger { Event: var name } when string.IsNullOrWhiteSpace(name):
                    throw Invalid(options, "An event trigger needs an event name.");
                case CronTrigger { Schedule: var schedule } when string.IsNullOrWhiteSpace(schedule):
                    throw Invalid(options, "A cron trigger needs a schedule.");
                case CronTrigger { Jitter: { } jitter } cron when jitter < MinPeriod || jitter > MaxCronJitter:
                    throw Invalid(options, $"The jitter of cron trigger \"{cron.Schedule}\" must be between 1s and 5m, but was {Describe(jitter)}.");
            }
        }
    }

    private static void ValidateConcurrency(FunctionOptions options)
    {
        if (options.Concurrency is not { } limits)
        {
            return;
        }

        if (limits.Count > MaxConcurrencyLimits)
        {
            throw Invalid(options, $"Concurrency allows at most {MaxConcurrencyLimits} limits, but {limits.Count} were given.");
        }

        foreach (var limit in limits)
        {
            if (limit.Limit <= 0)
            {
                throw Invalid(options, $"A concurrency limit must be greater than 0, but one was {limit.Limit}.");
            }

            if (!Enum.IsDefined(limit.Scope))
            {
                throw Invalid(options, $"{(int)limit.Scope} is not a valid ConcurrencyScope.");
            }

            if (limit.Scope != ConcurrencyScope.Function && string.IsNullOrWhiteSpace(limit.Key))
            {
                throw Invalid(options, $"A concurrency limit with the {limit.Scope} scope needs a Key. Only the Function scope can go without one.");
            }
        }
    }

    private static void ValidateThrottle(FunctionOptions options)
    {
        if (options.Throttle is not { } throttle)
        {
            return;
        }

        if (throttle.Limit <= 0)
        {
            throw Invalid(options, $"Throttle.Limit must be greater than 0, but was {throttle.Limit}.");
        }

        if (throttle.Period < MinPeriod)
        {
            throw Invalid(options, $"Throttle.Period must be at least 1s, but was {Describe(throttle.Period)}.");
        }

        if (throttle.Burst < 0)
        {
            throw Invalid(options, $"Throttle.Burst must not be negative, but was {throttle.Burst}.");
        }
    }

    private static void ValidateRateLimit(FunctionOptions options)
    {
        if (options.RateLimit is not { } rateLimit)
        {
            return;
        }

        if (rateLimit.Limit <= 0)
        {
            throw Invalid(options, $"RateLimit.Limit must be greater than 0, but was {rateLimit.Limit}.");
        }

        if (rateLimit.Period < MinPeriod || rateLimit.Period > MaxRateLimitPeriod)
        {
            throw Invalid(options, $"RateLimit.Period must be between 1s and 24h, but was {Describe(rateLimit.Period)}.");
        }
    }

    private static void ValidateDebounce(FunctionOptions options)
    {
        if (options.Debounce is not { } debounce)
        {
            return;
        }

        if (debounce.Period < MinPeriod || debounce.Period > MaxDebouncePeriod)
        {
            throw Invalid(options, $"Debounce.Period must be between 1s and 7d, but was {Describe(debounce.Period)}.");
        }

        if (debounce.Timeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            throw Invalid(options, $"Debounce.Timeout must be greater than zero, but was {Describe(timeout)}.");
        }
    }

    private static void ValidateBatchEvents(FunctionOptions options)
    {
        if (options.BatchEvents is not { } batch)
        {
            return;
        }

        if (batch.MaxSize < MinBatchSize)
        {
            throw Invalid(options, $"BatchEvents.MaxSize must be at least {MinBatchSize}, but was {batch.MaxSize}.");
        }

        // The upper limit depends on the Inngest plan, so it is left to the server.
        if (batch.Timeout < MinPeriod)
        {
            throw Invalid(options, $"BatchEvents.Timeout must be at least 1s, but was {Describe(batch.Timeout)}.");
        }

        // Inngest rejects a sync that combines batching with any of these.
        if (options.CancelOn is { Count: > 0 })
        {
            throw Invalid(options, "BatchEvents cannot be combined with CancelOn.");
        }

        if (options.Debounce is not null)
        {
            throw Invalid(options, "BatchEvents cannot be combined with Debounce.");
        }

        if (options.Singleton is not null)
        {
            throw Invalid(options, "BatchEvents cannot be combined with Singleton.");
        }

        if (options.Priority is not null)
        {
            throw Invalid(options, "BatchEvents cannot be combined with Priority.");
        }
    }

    private static void ValidatePriority(FunctionOptions options)
    {
        if (options.Priority is { } priority && string.IsNullOrWhiteSpace(priority.Run))
        {
            throw Invalid(options, "Priority.Run needs an expression, for example \"event.data.vip ? 120 : 0\".");
        }
    }

    private static void ValidateCancelOn(FunctionOptions options)
    {
        if (options.CancelOn is not { } cancellations)
        {
            return;
        }

        if (cancellations.Count > MaxCancellations)
        {
            throw Invalid(options, $"CancelOn allows at most {MaxCancellations} entries, but {cancellations.Count} were given.");
        }

        foreach (var cancellation in cancellations)
        {
            if (string.IsNullOrWhiteSpace(cancellation.Event))
            {
                throw Invalid(options, "A CancelOn entry needs an event name.");
            }

            if (cancellation.Timeout is { } timeout && timeout <= TimeSpan.Zero)
            {
                throw Invalid(options, $"The CancelOn timeout for \"{cancellation.Event}\" must be greater than zero, but was {Describe(timeout)}.");
            }
        }
    }

    private static void ValidateTimeouts(FunctionOptions options)
    {
        if (options.Timeouts is not { } timeouts)
        {
            return;
        }

        if (timeouts.Start is { } start && start <= TimeSpan.Zero)
        {
            throw Invalid(options, $"Timeouts.Start must be greater than zero, but was {Describe(start)}.");
        }

        if (timeouts.Finish is { } finish && finish <= TimeSpan.Zero)
        {
            throw Invalid(options, $"Timeouts.Finish must be greater than zero, but was {Describe(finish)}.");
        }
    }

    private static void ValidateSingleton(FunctionOptions options)
    {
        if (options.Singleton is { } singleton && !Enum.IsDefined(singleton.Mode))
        {
            throw Invalid(options, $"{(int)singleton.Mode} is not a valid SingletonMode.");
        }
    }

    // A type listed twice would resolve to the same scoped instance and wrap the function with it twice.
    private static void ValidateMiddleware(FunctionOptions options)
    {
        if (options.Middleware is not { } middleware)
        {
            return;
        }

        var seen = new HashSet<Type>();
        foreach (var item in middleware)
        {
            if (item is null)
            {
                throw Invalid(options, "Middleware contains a null entry.");
            }

            if (item.Type is { } type && !seen.Add(type))
            {
                throw Invalid(options, $"Middleware lists {type.Name} more than once.");
            }
        }
    }

    private static void ValidateCheckpointing(FunctionOptions options)
    {
        if (options.Checkpointing?.FindProblem() is { } problem)
        {
            throw Invalid(options, $"Checkpointing.{problem}");
        }
    }

    private static InvalidOperationException Invalid(FunctionOptions options, string problem) =>
        new($"Function \"{options.Id}\": {problem}");

    // TimeString would round a sub-second value up and reject a negative one, hiding what was actually given.
    private static string Describe(TimeSpan duration) =>
        duration >= MinPeriod ? TimeString.Format(duration) : $"{duration.TotalSeconds.ToString(CultureInfo.InvariantCulture)}s";
}
