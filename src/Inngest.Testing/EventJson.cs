// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Buffers;
using System.Text.Json;
using Inngest.Execution;
using Inngest.Json;

namespace Inngest.Testing;

// Writes events the way Inngest delivers them to a function or a wait: data defaults to an empty object, and an
// event without an id or a time gets the ones Inngest would give it.
internal static class EventJson
{
    public static byte[] Serialize(InngestEvent @event, JsonSerializerOptions options, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(@event);
        if (string.IsNullOrWhiteSpace(@event.Name))
        {
            throw new ArgumentException("An event needs a name.", nameof(@event));
        }

        JsonPayload.EnsureObject(@event.Data, options, "data", "event", @event.Name);
        JsonPayload.EnsureObject(@event.User, options, "user", "event", @event.Name);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = WireWriter.Create(buffer, options))
        {
            writer.WriteStartObject();
            writer.WriteString("name", @event.Name);
            writer.WritePropertyName("data");
            if (@event.Data is null)
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }
            else
            {
                Write(writer, @event.Data, options);
            }

            if (@event.User is not null)
            {
                writer.WritePropertyName("user");
                Write(writer, @event.User, options);
            }

            writer.WriteString("id", string.IsNullOrEmpty(@event.Id) ? Ulid.New(now) : @event.Id);
            writer.WriteNumber("ts", (@event.Timestamp ?? now).ToUnixTimeMilliseconds());
            if (!string.IsNullOrEmpty(@event.Version))
            {
                writer.WriteString("v", @event.Version);
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    // What InngestClient checks before it sends, so that an event it would refuse fails the same way here.
    public static void ValidateSend(IReadOnlyList<InngestEvent> events, JsonSerializerOptions options, DateTimeOffset now)
    {
        foreach (var @event in events)
        {
            if (@event is null)
            {
                throw new ArgumentException("The events must not contain null.");
            }

            if (@event.Name is { } name && name.StartsWith("inngest/", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"The event name '{name}' is reserved: names starting with 'inngest/' belong to Inngest.");
            }

            Serialize(@event, options, now);
        }
    }

    private static void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options.GetTypeInfo(value.GetType()));
}
