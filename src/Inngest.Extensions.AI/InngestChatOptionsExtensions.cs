// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Inngest;
using Inngest.Extensions.AI;

namespace Microsoft.Extensions.AI;

/// <summary>
/// Settings for one call to a chat client that UseInngestSteps wraps, carried in <see cref="ChatOptions.AdditionalProperties"/>.
/// The client removes them before the options reach the model, so a provider never sees them.
/// </summary>
public static class InngestChatOptionsExtensions
{
    /// <summary>
    /// Names the Inngest step of the call. Without a name, model calls are steps called "chat", and Inngest numbers repeats
    /// in the order they are made: "chat", "chat:1", "chat:2". Name a call when several run at once from threads of their own,
    /// which reach Inngest in no fixed order, or to tell calls apart in the Inngest UI. The id has to be the same on every
    /// replay and unique in the run, as the id of any step; a call made repeatedly with it is numbered the same way.
    /// </summary>
    public static ChatOptions WithInngestStepId(this ChatOptions options, string stepId)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(stepId);
        CallSettings.Set(options, CallSettings.Of(options) with { StepId = stepId });
        return options;
    }

    /// <summary>
    /// Publishes the text of a streaming call to the realtime topic as the model produces it, as IInngestRealtime does
    /// for a stream, so that a browser can show it before the step finishes. The step still returns the whole response
    /// once the model is done, and a replay publishes nothing. Failing to publish does not fail the model call.
    /// </summary>
    public static ChatOptions WithInngestRealtimeStream(this ChatOptions options, RealtimeTopic topic)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(topic);
        CallSettings.Set(options, CallSettings.Of(options) with { StreamTo = topic });
        return options;
    }
}
