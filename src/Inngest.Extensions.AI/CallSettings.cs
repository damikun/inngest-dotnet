// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Microsoft.Extensions.AI;

namespace Inngest.Extensions.AI;

// What the caller of one chat call asks of its step, carried in ChatOptions.AdditionalProperties because that is the
// only per-call channel IChatClient has. Providers send unknown additional properties to the model's API, so the client
// takes them out before it calls the inner client, on every path. They are strings, so that a stage outside the client
// that serializes the options, such as a cache or a logger, can write them, also under Native AOT.
internal sealed record CallSettings
{
    private const string StepIdKey = "Inngest.StepId";
    private const string ChannelKey = "Inngest.StreamChannel";
    private const string TopicKey = "Inngest.StreamTopic";

    public string? StepId { get; init; }

    public RealtimeTopic? StreamTo { get; init; }

    public static CallSettings Of(ChatOptions options) => new()
    {
        StepId = Text(options, StepIdKey),
        StreamTo = Text(options, ChannelKey) is { } channel && Text(options, TopicKey) is { } topic ? new RealtimeTopic(channel, topic) : null,
    };

    public static void Set(ChatOptions options, CallSettings settings)
    {
        var properties = options.AdditionalProperties ??= [];
        Write(properties, StepIdKey, settings.StepId);
        Write(properties, ChannelKey, settings.StreamTo?.Channel);
        Write(properties, TopicKey, settings.StreamTo?.Name);
    }

    // The options without the settings, and the settings. A copy, as the caller owns its options and may use them for
    // other calls; the same instance when they carry none.
    public static ChatOptions? Take(ChatOptions? options, out CallSettings settings)
    {
        settings = new CallSettings();
        if (options?.AdditionalProperties is not { } properties
            || !(properties.ContainsKey(StepIdKey) || properties.ContainsKey(ChannelKey) || properties.ContainsKey(TopicKey)))
        {
            return options;
        }

        settings = Of(options);
        var copy = options.Clone();
        copy.AdditionalProperties!.Remove(StepIdKey);
        copy.AdditionalProperties.Remove(ChannelKey);
        copy.AdditionalProperties.Remove(TopicKey);
        return copy;
    }

    private static string? Text(ChatOptions options, string key) =>
        options.AdditionalProperties is { } properties && properties.TryGetValue(key, out var value) ? value as string : null;

    private static void Write(AdditionalPropertiesDictionary properties, string key, string? value)
    {
        if (value is not null)
        {
            properties[key] = value;
        }
    }
}
