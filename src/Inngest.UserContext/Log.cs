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
}
