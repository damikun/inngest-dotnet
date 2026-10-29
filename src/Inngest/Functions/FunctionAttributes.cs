// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Reflection;
using Inngest.Internal;

namespace Inngest.Functions;

// Turns the attributes of a function class into FunctionOptions. Only time strings are checked here, since they have
// to become TimeSpans; whether Inngest accepts the values is checked at startup by FunctionOptionsValidator, as for
// options written in code.
internal static class FunctionAttributes
{
    public static FunctionOptions Read(Type functionType)
    {
        var function = functionType.GetCustomAttribute<InngestFunctionAttribute>(inherit: false)
            ?? throw new InvalidOperationException(
                $"{functionType.Name} has no [InngestFunction] attribute. Add one with the function's id, or pass FunctionOptions to AddFunction<{functionType.Name}>.");

        var id = function.Id;
        var triggers = new List<FunctionTrigger>();
        List<ConcurrencyLimit>? concurrency = null;
        List<CancelOn>? cancelOn = null;
        Throttle? throttle = null;
        RateLimit? rateLimit = null;
        Debounce? debounce = null;
        BatchEvents? batchEvents = null;
        Priority? priority = null;
        Singleton? singleton = null;
        FunctionTimeouts? timeouts = null;

        foreach (var attribute in functionType.GetCustomAttributes(inherit: false))
        {
            switch (attribute)
            {
                case EventTriggerAttribute trigger:
                    triggers.Add(new EventTrigger(trigger.Event) { If = trigger.If });
                    break;
                case CronTriggerAttribute cron:
                    triggers.Add(new CronTrigger(cron.Schedule)
                    {
                        Timezone = cron.Timezone,
                        Jitter = OptionalDuration(id, "CronTrigger", "Jitter", cron.Jitter),
                    });
                    break;
                case ConcurrencyAttribute limit:
                    (concurrency ??= []).Add(new ConcurrencyLimit(limit.Limit) { Key = limit.Key, Scope = limit.Scope });
                    break;
                case ThrottleAttribute value:
                    throttle = new Throttle(value.Limit, Duration(id, "Throttle", "Period", value.Period))
                    {
                        Burst = value.BurstValue,
                        Key = value.Key,
                    };
                    break;
                case RateLimitAttribute value:
                    rateLimit = new RateLimit(value.Limit, Duration(id, "RateLimit", "Period", value.Period)) { Key = value.Key };
                    break;
                case DebounceAttribute value:
                    debounce = new Debounce(Duration(id, "Debounce", "Period", value.Period))
                    {
                        Key = value.Key,
                        Timeout = OptionalDuration(id, "Debounce", "Timeout", value.Timeout),
                    };
                    break;
                case BatchEventsAttribute value:
                    batchEvents = new BatchEvents(value.MaxSize, Duration(id, "BatchEvents", "Timeout", value.Timeout))
                    {
                        Key = value.Key,
                        If = value.If,
                    };
                    break;
                case PriorityAttribute value:
                    priority = new Priority(value.Run);
                    break;
                case SingletonAttribute value:
                    singleton = new Singleton(value.Mode) { Key = value.Key };
                    break;
                case CancelOnAttribute cancellation:
                    (cancelOn ??= []).Add(new CancelOn(cancellation.Event)
                    {
                        If = cancellation.If,
                        Match = cancellation.Match,
                        Timeout = OptionalDuration(id, "CancelOn", "Timeout", cancellation.Timeout),
                    });
                    break;
                case TimeoutsAttribute value:
                    timeouts = new FunctionTimeouts
                    {
                        Start = OptionalDuration(id, "Timeouts", "Start", value.Start),
                        Finish = OptionalDuration(id, "Timeouts", "Finish", value.Finish),
                    };
                    break;
            }
        }

        return new FunctionOptions
        {
            Id = id,
            Name = function.Name,
            Triggers = triggers,
            Retries = function.RetriesValue,
            Idempotency = function.Idempotency,
            Concurrency = concurrency,
            Throttle = throttle,
            RateLimit = rateLimit,
            Debounce = debounce,
            BatchEvents = batchEvents,
            Priority = priority,
            CancelOn = cancelOn,
            Timeouts = timeouts,
            Singleton = singleton,
        };
    }

    private static TimeSpan Duration(string functionId, string attribute, string property, string? value) =>
        TimeString.TryParse(value, out var duration)
            ? duration
            : throw new InvalidOperationException(
                $"Function \"{functionId}\": {property} of [{attribute}] is \"{value}\", which is not a time string. " +
                "Use whole numbers with w, d, h, m or s, such as \"30s\", \"5m\" or \"1h30m\".");

    private static TimeSpan? OptionalDuration(string functionId, string attribute, string property, string? value) =>
        value is null ? null : Duration(functionId, attribute, property, value);
}
