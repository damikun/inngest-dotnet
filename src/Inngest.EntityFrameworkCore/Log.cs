// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Microsoft.Extensions.Logging;

namespace Inngest.EntityFrameworkCore;

// Event ids 800-819.
internal static partial class Log
{
    [LoggerMessage(EventId = 800, Level = LogLevel.Information, Message = "The Inngest outbox of {Context} is running. It looks for events every {PollInterval}, and at once after a save that added some.")]
    public static partial void Started(ILogger logger, string context, TimeSpan pollInterval);

    [LoggerMessage(EventId = 801, Level = LogLevel.Information, Message = "The Inngest outbox of {Context} does not send events in this process (DispatcherEnabled is false).")]
    public static partial void Disabled(ILogger logger, string context);

    [LoggerMessage(EventId = 802, Level = LogLevel.Debug, Message = "Sent {Count} events of the outbox of {Context} to Inngest.")]
    public static partial void Sent(ILogger logger, int count, string context);

    [LoggerMessage(EventId = 803, Level = LogLevel.Warning, Message = "Sending {Count} events of the outbox of {Context} to Inngest failed: {Reason} They stay in the table and are tried again, the first of them from {RetryAt:u}.")]
    public static partial void SendFailed(ILogger logger, int count, string context, string reason, DateTime retryAt);

    [LoggerMessage(EventId = 804, Level = LogLevel.Warning, Message = "Inngest refused a request with {Count} events of the outbox of {Context}: {Reason} Sending them one at a time to find out which one it objects to.")]
    public static partial void Isolating(ILogger logger, int count, string context, string reason);

    [LoggerMessage(EventId = 805, Level = LogLevel.Error, Message = "Inngest refuses event {EventId} ({EventName}) of the outbox of {Context}: {Reason} It stays in the table, tried again from {RetryAt:u}; it will not go through until the row is fixed or deleted.")]
    public static partial void Refused(ILogger logger, string eventId, string eventName, string context, string reason, DateTime retryAt);

    [LoggerMessage(EventId = 806, Level = LogLevel.Error, Message = "The Inngest outbox of {Context} could not read or update its table. Trying again in {Delay}.")]
    public static partial void PassFailed(ILogger logger, Exception exception, string context, TimeSpan delay);

    [LoggerMessage(EventId = 807, Level = LogLevel.Debug, Message = "Deleted {Count} events of the outbox of {Context} that were sent more than {Retention} ago.")]
    public static partial void CleanedUp(ILogger logger, int count, string context, TimeSpan retention);

    [LoggerMessage(EventId = 808, Level = LogLevel.Warning, Message = "Deleting the sent events of the outbox of {Context} failed. Trying again after the next interval.")]
    public static partial void CleanupFailed(ILogger logger, Exception exception, string context);

    [LoggerMessage(EventId = 809, Level = LogLevel.Debug, Message = "Gave {Count} claimed events of the outbox of {Context} back, so that the next dispatcher takes them at once.")]
    public static partial void Released(ILogger logger, int count, string context);

    [LoggerMessage(EventId = 810, Level = LogLevel.Information, Message = "{Context} is not set up with UseInngestOutbox, so a save in this process does not wake the dispatcher: events are found at the next poll, within {PollInterval}. Nothing else is lost by that.")]
    public static partial void NoWakeUp(ILogger logger, string context, TimeSpan pollInterval);
}
