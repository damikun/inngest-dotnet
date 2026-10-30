// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Globalization;
using Inngest.Internal;
using Inngest.Registration;

namespace Inngest.Functions;

// Applies the limits Inngest enforces on a function, so a mistake fails at startup and names the function
// instead of failing a sync with an error about the whole app. It looks for every problem, not just the first.
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
        var problems = new ConfigurationProblems();
        Validate(options, problems);
        problems.ThrowIfAny();
    }

    public static void Validate(FunctionOptions options, ConfigurationProblems problems)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(problems);

        // Every other problem names the function by its id, so they wait until it has one.
        if (string.IsNullOrWhiteSpace(options.Id))
        {
            var which = string.IsNullOrWhiteSpace(options.Name) ? "A function" : $"The function named \"{options.Name}\"";
            problems.Add($"{which} has no Id. Set FunctionOptions.Id to an id that is unique in the app and never changes.");
            return;
        }

        if (options.Retries is < 0 or > MaxRetries)
        {
            problems.Add(options, $"Retries must be between 0 and {MaxRetries}, but was {options.Retries}.");
        }

        ValidateTriggers(options, problems);
        ValidateConcurrency(options, problems);
        ValidateThrottle(options, problems);
        ValidateRateLimit(options, problems);
        ValidateDebounce(options, problems);
        ValidateBatchEvents(options, problems);
        ValidatePriority(options, problems);
        ValidateCancelOn(options, problems);
        ValidateTimeouts(options, problems);
        ValidateSingleton(options, problems);
        ValidateMiddleware(options, problems);
        ValidateCheckpointing(options, problems);
    }

    // An empty trigger list is valid: the function then runs only when another function invokes it.
    private static void ValidateTriggers(FunctionOptions options, ConfigurationProblems problems)
    {
        if (options.Triggers.Count > MaxTriggers)
        {
            problems.Add(options, $"A function can have at most {MaxTriggers} triggers, but has {options.Triggers.Count}.");
        }

        var events = new HashSet<(string Event, string? If)>();
        var schedules = new HashSet<string>(StringComparer.Ordinal);
        foreach (var trigger in options.Triggers)
        {
            switch (trigger)
            {
                case EventTrigger { Event: var name } when string.IsNullOrWhiteSpace(name):
                    problems.Add(options, "An event trigger needs an event name.");
                    break;
                case CronTrigger { Schedule: var schedule } when string.IsNullOrWhiteSpace(schedule):
                    problems.Add(options, "A cron trigger needs a schedule.");
                    break;
                case CronTrigger { Jitter: { } jitter } cron when jitter < MinPeriod || jitter > MaxCronJitter:
                    problems.Add(options, $"The jitter of cron trigger \"{cron.Schedule}\" must be between 1s and 5m, but was {Describe(jitter)}.");
                    break;
            }

            // Inngest refuses a sync that has the same trigger twice, and the same cron schedule twice even with
            // different jitter.
            if (trigger is EventTrigger eventTrigger
                && !events.Add((eventTrigger.Event, string.IsNullOrEmpty(eventTrigger.If) ? null : eventTrigger.If)))
            {
                problems.Add(options, $"The event trigger \"{eventTrigger.Event}\" is given twice.");
            }

            if (trigger is CronTrigger cronTrigger && !schedules.Add(RegistrationWriter.CronSchedule(cronTrigger)))
            {
                problems.Add(options, $"The cron schedule \"{cronTrigger.Schedule}\" is given twice, which Inngest refuses even with different jitter.");
            }
        }
    }

    private static void ValidateConcurrency(FunctionOptions options, ConfigurationProblems problems)
    {
        if (options.Concurrency is not { } limits)
        {
            return;
        }

        if (limits.Count > MaxConcurrencyLimits)
        {
            problems.Add(options, $"Concurrency allows at most {MaxConcurrencyLimits} limits, but {limits.Count} were given.");
        }

        foreach (var limit in limits)
        {
            // 0 means no limit, as in the Inngest docs.
            if (limit.Limit < 0)
            {
                problems.Add(options, $"A concurrency limit cannot be negative, but one was {limit.Limit}. Use 0 for no limit.");
            }

            if (!Enum.IsDefined(limit.Scope))
            {
                problems.Add(options, $"{(int)limit.Scope} is not a valid ConcurrencyScope.");
            }
            else if (limit.Scope != ConcurrencyScope.Function && string.IsNullOrWhiteSpace(limit.Key))
            {
                problems.Add(options, $"A concurrency limit with the {limit.Scope} scope needs a Key. Only the Function scope can go without one.");
            }
        }
    }

    private static void ValidateThrottle(FunctionOptions options, ConfigurationProblems problems)
    {
        if (options.Throttle is not { } throttle)
        {
            return;
        }

        if (throttle.Limit <= 0)
        {
            problems.Add(options, $"Throttle.Limit must be greater than 0, but was {throttle.Limit}.");
        }

        if (throttle.Period < MinPeriod)
        {
            problems.Add(options, $"Throttle.Period must be at least 1s, but was {Describe(throttle.Period)}.");
        }

        if (throttle.Burst < 0)
        {
            problems.Add(options, $"Throttle.Burst must not be negative, but was {throttle.Burst}.");
        }
    }

    private static void ValidateRateLimit(FunctionOptions options, ConfigurationProblems problems)
    {
        if (options.RateLimit is not { } rateLimit)
        {
            return;
        }

        if (rateLimit.Limit <= 0)
        {
            problems.Add(options, $"RateLimit.Limit must be greater than 0, but was {rateLimit.Limit}.");
        }

        if (rateLimit.Period < MinPeriod || rateLimit.Period > MaxRateLimitPeriod)
        {
            problems.Add(options, $"RateLimit.Period must be between 1s and 24h, but was {Describe(rateLimit.Period)}.");
        }
    }

    private static void ValidateDebounce(FunctionOptions options, ConfigurationProblems problems)
    {
        if (options.Debounce is not { } debounce)
        {
            return;
        }

        if (debounce.Period < MinPeriod || debounce.Period > MaxDebouncePeriod)
        {
            problems.Add(options, $"Debounce.Period must be between 1s and 7d, but was {Describe(debounce.Period)}.");
        }

        if (debounce.Timeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            problems.Add(options, $"Debounce.Timeout must be greater than zero, but was {Describe(timeout)}.");
        }
    }

    private static void ValidateBatchEvents(FunctionOptions options, ConfigurationProblems problems)
    {
        if (options.BatchEvents is not { } batch)
        {
            return;
        }

        if (batch.MaxSize < MinBatchSize)
        {
            problems.Add(options, $"BatchEvents.MaxSize must be at least {MinBatchSize}, but was {batch.MaxSize}.");
        }

        // The upper limit depends on the Inngest plan, so it is left to the server.
        if (batch.Timeout < MinPeriod)
        {
            problems.Add(options, $"BatchEvents.Timeout must be at least 1s, but was {Describe(batch.Timeout)}.");
        }

        // Inngest rejects a sync that combines batching with any of these.
        if (options.CancelOn is { Count: > 0 })
        {
            problems.Add(options, "BatchEvents cannot be combined with CancelOn.");
        }

        if (options.Debounce is not null)
        {
            problems.Add(options, "BatchEvents cannot be combined with Debounce.");
        }

        if (options.Singleton is not null)
        {
            problems.Add(options, "BatchEvents cannot be combined with Singleton.");
        }

        if (options.Priority is not null)
        {
            problems.Add(options, "BatchEvents cannot be combined with Priority.");
        }
    }

    private static void ValidatePriority(FunctionOptions options, ConfigurationProblems problems)
    {
        if (options.Priority is { } priority && string.IsNullOrWhiteSpace(priority.Run))
        {
            problems.Add(options, "Priority.Run needs an expression, for example \"event.data.vip ? 120 : 0\".");
        }
    }

    private static void ValidateCancelOn(FunctionOptions options, ConfigurationProblems problems)
    {
        if (options.CancelOn is not { } cancellations)
        {
            return;
        }

        if (cancellations.Count > MaxCancellations)
        {
            problems.Add(options, $"CancelOn allows at most {MaxCancellations} entries, but {cancellations.Count} were given.");
        }

        foreach (var cancellation in cancellations)
        {
            // The timeout problem names the event, so an entry without one stops here.
            if (string.IsNullOrWhiteSpace(cancellation.Event))
            {
                problems.Add(options, "A CancelOn entry needs an event name.");
                continue;
            }

            if (cancellation.Timeout is { } timeout && timeout <= TimeSpan.Zero)
            {
                problems.Add(options, $"The CancelOn timeout for \"{cancellation.Event}\" must be greater than zero, but was {Describe(timeout)}.");
            }
        }
    }

    private static void ValidateTimeouts(FunctionOptions options, ConfigurationProblems problems)
    {
        if (options.Timeouts is not { } timeouts)
        {
            return;
        }

        if (timeouts.Start is { } start && start <= TimeSpan.Zero)
        {
            problems.Add(options, $"Timeouts.Start must be greater than zero, but was {Describe(start)}.");
        }

        if (timeouts.Finish is { } finish && finish <= TimeSpan.Zero)
        {
            problems.Add(options, $"Timeouts.Finish must be greater than zero, but was {Describe(finish)}.");
        }
    }

    private static void ValidateSingleton(FunctionOptions options, ConfigurationProblems problems)
    {
        if (options.Singleton is { } singleton && !Enum.IsDefined(singleton.Mode))
        {
            problems.Add(options, $"{(int)singleton.Mode} is not a valid SingletonMode.");
        }
    }

    // A type listed twice would resolve to the same scoped instance and wrap the function with it twice.
    private static void ValidateMiddleware(FunctionOptions options, ConfigurationProblems problems)
    {
        if (options.Middleware is not { } middleware)
        {
            return;
        }

        var seen = new HashSet<Type>();
        var repeated = new HashSet<Type>();
        foreach (var item in middleware)
        {
            if (item is null)
            {
                problems.Add(options, "Middleware contains a null entry.");
            }
            else if (item.Type is { } type && !seen.Add(type) && repeated.Add(type))
            {
                problems.Add(options, $"Middleware lists {type.Name} more than once.");
            }
        }
    }

    private static void ValidateCheckpointing(FunctionOptions options, ConfigurationProblems problems)
    {
        if (options.Checkpointing?.FindProblem() is { } problem)
        {
            problems.Add(options, $"Checkpointing.{problem}");
        }
    }

    // TimeString would round a sub-second value up and reject a negative one, hiding what was actually given.
    private static string Describe(TimeSpan duration) =>
        duration >= MinPeriod ? TimeString.Format(duration) : $"{duration.TotalSeconds.ToString(CultureInfo.InvariantCulture)}s";
}
