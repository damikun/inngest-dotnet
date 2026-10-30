// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Microsoft.Extensions.AI;

namespace Inngest.Extensions.AI;

// What the caller of one chat call asks of its step, carried in ChatOptions.AdditionalProperties because that is the
// only per-call channel IChatClient has. Providers send unknown additional properties to the model's API, so the client
// takes it out before it calls the inner client, on every path.
internal sealed record CallSettings
{
    private const string Key = "Inngest.CallSettings";

    public string? StepId { get; init; }

    public RealtimeTopic? StreamTo { get; init; }

    public static CallSettings Of(ChatOptions options) =>
        options.AdditionalProperties is { } properties && properties.TryGetValue(Key, out var value) && value is CallSettings settings
            ? settings
            : new CallSettings();

    public static void Set(ChatOptions options, CallSettings settings) => (options.AdditionalProperties ??= [])[Key] = settings;

    // The options without the settings, and the settings. A copy, as the caller owns its options and may use them for
    // other calls; the same instance when they carry none.
    public static ChatOptions? Take(ChatOptions? options, out CallSettings settings)
    {
        settings = new CallSettings();
        if (options?.AdditionalProperties is not { } properties || !properties.ContainsKey(Key))
        {
            return options;
        }

        settings = Of(options);
        var copy = options.Clone();
        copy.AdditionalProperties!.Remove(Key);
        return copy;
    }
}
