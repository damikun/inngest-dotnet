// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Microsoft.Extensions.Logging;

namespace Inngest.Webhooks;

// Nothing here takes a secret, a signature, a header or a body. A request that is rejected is described by what is
// wrong with it, in words of ours.
internal static partial class Log
{
    [LoggerMessage(EventId = 700, Level = LogLevel.Warning, Message = "Rejected a {Provider} webhook: {Reason}")]
    public static partial void Rejected(ILogger logger, string provider, string reason);

    [LoggerMessage(EventId = 701, Level = LogLevel.Warning, Message = "Rejected a {Provider} webhook with a body over the limit of {Limit} bytes")]
    public static partial void BodyTooLarge(ILogger logger, string provider, int limit);

    [LoggerMessage(EventId = 702, Level = LogLevel.Warning, Message = "Rejected a {Provider} webhook whose body is not JSON. Its signature is valid, so check the content type the provider sends.")]
    public static partial void NotJson(ILogger logger, string provider);

    [LoggerMessage(EventId = 703, Level = LogLevel.Warning, Message = "Rejected a {Provider} webhook that is not something an event can be made of: its type or its id is missing or malformed, or its payload is not an object")]
    public static partial void NotMapped(ILogger logger, string provider);

    [LoggerMessage(EventId = 704, Level = LogLevel.Debug, Message = "Skipped a {Provider} webhook: the transform returned no event")]
    public static partial void Skipped(ILogger logger, string provider);

    [LoggerMessage(EventId = 705, Level = LogLevel.Debug, Message = "Sent event {EventName} with id {EventId} for a {Provider} webhook")]
    public static partial void Sent(ILogger logger, string provider, string eventName, string? eventId);

    [LoggerMessage(EventId = 706, Level = LogLevel.Error, Message = "Could not send the event of a {Provider} webhook to Inngest. The webhook is answered with a failure, so the provider delivers it again.")]
    public static partial void SendFailed(ILogger logger, Exception exception, string provider);

    [LoggerMessage(EventId = 707, Level = LogLevel.Error, Message = "Inngest did not accept the event of a {Provider} webhook within {Timeout}. The webhook is answered with a failure, so the provider delivers it again.")]
    public static partial void SendTimedOut(ILogger logger, string provider, TimeSpan timeout);

    [LoggerMessage(EventId = 708, Level = LogLevel.Warning, Message = "Rejected a {Provider} webhook whose event Inngest's client refused: {Reason}")]
    public static partial void EventRefused(ILogger logger, string provider, string reason);

    [LoggerMessage(EventId = 709, Level = LogLevel.Error, Message = "The verifier or the transform of the {Provider} webhook threw an exception")]
    public static partial void Failed(ILogger logger, Exception exception, string provider);
}
