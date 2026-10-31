// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Microsoft.Extensions.Logging;

namespace Inngest.UserContext;

// Event ids 710-719. Nothing here ever logs a claim or the protected text.
internal static partial class Log
{
    [LoggerMessage(EventId = 710, Level = LogLevel.Warning, Message = "The user in event {EventName} is not used because {Reason}. The run continues without a user.")]
    public static partial void UserNotUsed(ILogger logger, string eventName, string reason);

    [LoggerMessage(EventId = 711, Level = LogLevel.Warning, Message = "The claims of the user are larger than UserContextOptions.MaxSize ({MaxSize} bytes), so event {EventName} is sent without a user.")]
    public static partial void UserTooLarge(ILogger logger, string eventName, int maxSize);

    [LoggerMessage(EventId = 712, Level = LogLevel.Information, Message = "The user expires at {ExpiresAt:O}, before event {EventName} can start a run, so it is sent without a user.")]
    public static partial void UserExpiresBeforeEvent(ILogger logger, string eventName, DateTimeOffset expiresAt);

    [LoggerMessage(EventId = 714, Level = LogLevel.Warning, Message = "Run id {RunId} is not a ULID, so it says when the run was created, and the user of event {EventName} is judged by the time of each call instead. It may expire while the run goes on.")]
    public static partial void RunIdHasNoTime(ILogger logger, string runId, string eventName);

    [LoggerMessage(EventId = 713, Level = LogLevel.Information, Message = "The user expires at {ExpiresAt:O}, before the invocation of {Function} can start a run, so it is passed on without a user.")]
    public static partial void UserExpiresBeforeInvocation(ILogger logger, string function, DateTimeOffset expiresAt);
}
