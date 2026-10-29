// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Encodings.Web;
using System.Text.Json;
using Inngest.Internal;

namespace Inngest.Registration;

// Writes the body of a sync: what Inngest needs to know about the app and each of its functions. Options that are
// not set are left out, so that Inngest applies its own defaults.
internal static class RegistrationWriter
{
    private const string PingDeployType = "ping";
    private const string ConnectDeployType = "connect";
    private const string StepId = "step";

    // Keeps single quotes and operators in expressions readable on the wire. The body is never embedded in HTML.
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static void Write(
        PooledBufferWriter output,
        InngestSettings settings,
        IEnumerable<RegisteredFunction> functions,
        Uri serveUrl,
        string framework,
        string? deployType)
    {
        deployType ??= PingDeployType;
        var url = new ServeUrl(serveUrl);

        // A Connect app is called through the gateway's websocket rather than over HTTP, and Inngest only treats
        // the sync as one when the connect capability comes with it.
        var isConnect = deployType == ConnectDeployType;
        var runtimeType = isConnect ? "ws" : "http";

        using var json = new Utf8JsonWriter(output, WriterOptions);
        json.WriteStartObject();
        json.WriteString("url", url.App);
        json.WriteString("deployType", deployType);
        json.WriteString("appName", settings.AppId);
        WriteIfSet(json, "appVersion", settings.AppVersion);
        json.WriteString("sdk", SdkInfo.RegistrationValue);
        json.WriteString("v", "0.1");
        WriteIfSet(json, "framework", framework);
        json.WriteStartObject("capabilities");
        json.WriteString("trust_probe", "v1");
        if (isConnect)
        {
            json.WriteString("connect", "v1");
        }

        json.WriteEndObject();

        json.WriteStartArray("functions");
        foreach (var function in functions)
        {
            WriteFunction(json, function, url, runtimeType);
        }

        json.WriteEndArray();
        json.WriteEndObject();
        json.Flush();
    }

    private static void WriteFunction(Utf8JsonWriter json, RegisteredFunction function, ServeUrl url, string runtimeType)
    {
        var options = function.Options;

        json.WriteStartObject();
        json.WriteString("id", function.CompositeId);
        json.WriteString("name", string.IsNullOrWhiteSpace(options.Name) ? options.Id : options.Name);
        WriteTriggers(json, options.Triggers);
        WriteSteps(json, function, url, runtimeType);
        WriteConcurrency(json, options.Concurrency);
        WriteThrottle(json, options.Throttle);
        WriteRateLimit(json, options.RateLimit);
        WriteIfSet(json, "idempotency", options.Idempotency);
        WriteDebounce(json, options.Debounce);
        WriteBatchEvents(json, options.BatchEvents);
        WritePriority(json, options.Priority);
        WriteCancellations(json, options.CancelOn);
        WriteTimeouts(json, options.Timeouts);
        WriteSingleton(json, options.Singleton);
        json.WriteEndObject();
    }

    private static void WriteTriggers(Utf8JsonWriter json, IReadOnlyList<FunctionTrigger> triggers)
    {
        json.WriteStartArray("triggers");
        foreach (var trigger in triggers)
        {
            json.WriteStartObject();
            switch (trigger)
            {
                case EventTrigger eventTrigger:
                    json.WriteString("event", eventTrigger.Event);
                    WriteIfSet(json, "expression", eventTrigger.If);
                    break;
                case CronTrigger cronTrigger:
                    json.WriteString("cron", CronSchedule(cronTrigger));
                    if (cronTrigger.Jitter is { } jitter)
                    {
                        json.WriteString("jitter", TimeString.Format(jitter));
                    }

                    break;
                default:
                    throw new NotSupportedException($"Trigger type {trigger.GetType().Name} is not supported.");
            }

            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    // Inngest reads the time zone from a "TZ=" or "CRON_TZ=" prefix. A prefix the schedule already has wins.
    private static string CronSchedule(CronTrigger trigger)
    {
        var hasTimezone = trigger.Schedule.StartsWith("TZ=", StringComparison.Ordinal)
            || trigger.Schedule.StartsWith("CRON_TZ=", StringComparison.Ordinal);

        return string.IsNullOrWhiteSpace(trigger.Timezone) || hasTimezone
            ? trigger.Schedule
            : $"TZ={trigger.Timezone} {trigger.Schedule}";
    }

    // Every function registers one step, whose call starts a run. The steps in the function's code are found while
    // it runs.
    private static void WriteSteps(Utf8JsonWriter json, RegisteredFunction function, ServeUrl url, string runtimeType)
    {
        json.WriteStartObject("steps");
        json.WriteStartObject(StepId);
        json.WriteString("id", StepId);
        json.WriteString("name", StepId);

        json.WriteStartObject("runtime");
        json.WriteString("type", runtimeType);
        json.WriteString("url", url.Step(function.CompositeId));
        json.WriteEndObject();

        // The attempts field holds the number of retries, not attempts.
        if (function.Options.Retries is { } retries)
        {
            json.WriteStartObject("retries");
            json.WriteNumber("attempts", retries);
            json.WriteEndObject();
        }

        json.WriteEndObject();
        json.WriteEndObject();
    }

    private static void WriteConcurrency(Utf8JsonWriter json, IReadOnlyList<ConcurrencyLimit>? limits)
    {
        if (limits is not { Count: > 0 })
        {
            return;
        }

        json.WriteStartArray("concurrency");
        foreach (var limit in limits)
        {
            json.WriteStartObject();
            json.WriteNumber("limit", limit.Limit);
            WriteIfSet(json, "key", limit.Key);
            json.WriteString("scope", limit.Scope switch
            {
                ConcurrencyScope.Function => "fn",
                ConcurrencyScope.Environment => "env",
                ConcurrencyScope.Account => "account",
                _ => throw new ArgumentOutOfRangeException(nameof(limits), limit.Scope, "Unknown concurrency scope."),
            });
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    private static void WriteThrottle(Utf8JsonWriter json, Throttle? throttle)
    {
        if (throttle is null)
        {
            return;
        }

        json.WriteStartObject("throttle");
        json.WriteNumber("limit", throttle.Limit);
        json.WriteString("period", TimeString.Format(throttle.Period));
        if (throttle.Burst is { } burst)
        {
            json.WriteNumber("burst", burst);
        }

        WriteIfSet(json, "key", throttle.Key);
        json.WriteEndObject();
    }

    private static void WriteRateLimit(Utf8JsonWriter json, RateLimit? rateLimit)
    {
        if (rateLimit is null)
        {
            return;
        }

        json.WriteStartObject("rateLimit");
        json.WriteNumber("limit", rateLimit.Limit);
        json.WriteString("period", TimeString.Format(rateLimit.Period));
        WriteIfSet(json, "key", rateLimit.Key);
        json.WriteEndObject();
    }

    private static void WriteDebounce(Utf8JsonWriter json, Debounce? debounce)
    {
        if (debounce is null)
        {
            return;
        }

        json.WriteStartObject("debounce");
        json.WriteString("period", TimeString.Format(debounce.Period));
        WriteIfSet(json, "key", debounce.Key);
        if (debounce.Timeout is { } timeout)
        {
            json.WriteString("timeout", TimeString.Format(timeout));
        }

        json.WriteEndObject();
    }

    private static void WriteBatchEvents(Utf8JsonWriter json, BatchEvents? batch)
    {
        if (batch is null)
        {
            return;
        }

        json.WriteStartObject("batchEvents");
        json.WriteNumber("maxSize", batch.MaxSize);

        // Inngest reads this timeout with Go's time.ParseDuration, which has no day or week units. The 60s limit
        // keeps the string to seconds and minutes.
        json.WriteString("timeout", TimeString.Format(batch.Timeout));
        WriteIfSet(json, "key", batch.Key);
        WriteIfSet(json, "if", batch.If);
        json.WriteEndObject();
    }

    private static void WritePriority(Utf8JsonWriter json, Priority? priority)
    {
        if (priority is null)
        {
            return;
        }

        json.WriteStartObject("priority");
        json.WriteString("run", priority.Run);
        json.WriteEndObject();
    }

    private static void WriteCancellations(Utf8JsonWriter json, IReadOnlyList<CancelOn>? cancellations)
    {
        if (cancellations is not { Count: > 0 })
        {
            return;
        }

        json.WriteStartArray("cancel");
        foreach (var cancellation in cancellations)
        {
            json.WriteStartObject();
            json.WriteString("event", cancellation.Event);
            if (cancellation.Timeout is { } timeout)
            {
                json.WriteString("timeout", TimeString.Format(timeout));
            }

            WriteIfSet(json, "if", MatchExpression.Combine(cancellation.Match, cancellation.If));
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    private static void WriteTimeouts(Utf8JsonWriter json, FunctionTimeouts? timeouts)
    {
        if (timeouts is null || (timeouts.Start is null && timeouts.Finish is null))
        {
            return;
        }

        json.WriteStartObject("timeouts");
        if (timeouts.Start is { } start)
        {
            json.WriteString("start", TimeString.Format(start));
        }

        if (timeouts.Finish is { } finish)
        {
            json.WriteString("finish", TimeString.Format(finish));
        }

        json.WriteEndObject();
    }

    private static void WriteSingleton(Utf8JsonWriter json, Singleton? singleton)
    {
        if (singleton is null)
        {
            return;
        }

        json.WriteStartObject("singleton");
        json.WriteString("mode", singleton.Mode switch
        {
            SingletonMode.Skip => "skip",
            SingletonMode.Cancel => "cancel",
            _ => throw new ArgumentOutOfRangeException(nameof(singleton), singleton.Mode, "Unknown singleton mode."),
        });
        WriteIfSet(json, "key", singleton.Key);
        json.WriteEndObject();
    }

    private static void WriteIfSet(Utf8JsonWriter json, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            json.WriteString(name, value);
        }
    }
}
