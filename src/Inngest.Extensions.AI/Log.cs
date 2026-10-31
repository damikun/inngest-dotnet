// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Microsoft.Extensions.Logging;

namespace Inngest.Extensions.AI;

internal static partial class Log
{
    [LoggerMessage(EventId = 600, Level = LogLevel.Warning, Message = "The input of step {StepId} could not be recorded, so it runs without: {Reason}")]
    public static partial void InputNotRecorded(ILogger logger, string stepId, string reason);

    [LoggerMessage(EventId = 601, Level = LogLevel.Warning, Message = "The text of step {StepId} could not be streamed to topic {Topic} of channel {Channel}, and the model call goes on without: {Reason}")]
    public static partial void StreamNotPublished(ILogger logger, string stepId, string topic, string channel, string reason);

    [LoggerMessage(EventId = 602, Level = LogLevel.Warning, Message = "Step {StepId} of run {RunId} saved the result of a call with another input than this call has. Calls that reach the chat client in no fixed order, such as ones made after a request that finishes at varying times, can be handed each other's results. Name the calls with ChatOptions.WithInngestStepId, so that their ids do not depend on timing; the tools of a named conversation carry its name.")]
    public static partial void InputDiffers(ILogger logger, string stepId, string runId);

    [LoggerMessage(EventId = 603, Level = LogLevel.Warning, Message = "The response of step {StepId} has additional properties that are not JSON, which are left out of what the step saves: {Reason}")]
    public static partial void AdditionalPropertiesLeftOut(ILogger logger, string stepId, string reason);

    [LoggerMessage(EventId = 604, Level = LogLevel.Warning, Message = "The text of step {StepId} is longer than the {Chunks} chunks of up to {Bytes} bytes that Inngest takes in a stream, so the rest is not published to topic {Topic} of channel {Channel}.")]
    public static partial void StreamTooLong(ILogger logger, string stepId, int chunks, int bytes, string topic, string channel);
}
