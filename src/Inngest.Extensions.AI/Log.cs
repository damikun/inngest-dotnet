// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Microsoft.Extensions.Logging;

namespace Inngest.Extensions.AI;

internal static partial class Log
{
    [LoggerMessage(EventId = 600, Level = LogLevel.Warning, Message = "The input of step {StepId} could not be recorded, so it runs without: {Reason}")]
    public static partial void InputNotRecorded(ILogger logger, string stepId, string reason);

    [LoggerMessage(EventId = 601, Level = LogLevel.Warning, Message = "The text of step {StepId} could not be streamed to topic {Topic} of channel {Channel}, and the model call goes on without: {Reason}")]
    public static partial void StreamNotPublished(ILogger logger, string stepId, string topic, string channel, string reason);
}
