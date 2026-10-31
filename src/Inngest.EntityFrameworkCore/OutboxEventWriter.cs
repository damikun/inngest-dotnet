// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Buffers;
using System.Text;
using System.Text.Json;
using Inngest.Events;
using Inngest.Json;

namespace Inngest.EntityFrameworkCore;

// Turns an event into the row that holds it. Whatever is wrong with the event is found here, while the code that added
// it is still on the stack, and not by the dispatcher long after, where the only thing left to do with a row that can
// never be sent is to try it again. The checks are the ones the client makes when it sends an event.
internal sealed class OutboxEventWriter(JsonSerializerOptions serializer, TimeProvider time)
{
    // The length of the columns that hold text the caller chooses.
    public const int MaxTextLength = 255;

    public InngestOutboxMessage Create(InngestEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        CheckName(@event.Name);

        var owner = $"event \"{@event.Name}\"";
        JsonPayload.EnsureObject(@event.Data, serializer, "data", "event", @event.Name);
        JsonPayload.EnsureObject(@event.User, serializer, "user", "event", @event.Name);
        EventSessions.Check(@event.Meta, owner);

        // The SDK fills these in for events sent from a run, which this is not.
        if (@event.Meta?.PropagatedSessions is { Count: > 0 })
        {
            throw new ArgumentException($"The meta of {owner} sets PropagatedSessions, which only applies to events sent from a run. Use Sessions.");
        }

        var now = time.GetUtcNow();
        var id = string.IsNullOrEmpty(@event.Id) ? OutboxEventId.New(now) : @event.Id;
        CheckLength(id, "id", owner);
        CheckLength(@event.Version, "version", owner);

        return new InngestOutboxMessage
        {
            Id = id,
            Name = @event.Name,
            Data = @event.Data is null ? "{}" : Serialize(@event.Data),
            User = @event.User is null ? null : Serialize(@event.User),
            Version = string.IsNullOrEmpty(@event.Version) ? null : @event.Version,
            Sessions = SerializeSessions(@event.Meta?.Sessions),

            // The time of the call that added the event, as for an event the client sends, and not the time the dispatcher
            // gets to it, which is later by however long Inngest was out of reach.
            Timestamp = (@event.Timestamp ?? now).UtcDateTime,

            // Never the event's own, which the caller may set to any time.
            CreatedAt = now.UtcDateTime,
            NextAttemptAt = now.UtcDateTime,
        };
    }

    private static void CheckName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("An event needs a name.");
        }

        if (name.StartsWith("inngest/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"The event name '{name}' is reserved: names starting with 'inngest/' belong to Inngest.");
        }

        CheckLength(name, "name", $"event \"{name}\"");
    }

    private static void CheckLength(string? value, string what, string owner)
    {
        if (value is { Length: > MaxTextLength })
        {
            throw new ArgumentException($"The {what} of {owner} is {value.Length} characters long; the outbox stores at most {MaxTextLength}.");
        }
    }

    // By the runtime type, through the options' metadata, as the client does, so that what works for sending works here.
    private string Serialize(object value) => JsonSerializer.Serialize(value, serializer.GetTypeInfo(value.GetType()));

    private static string? SerializeSessions(IReadOnlyDictionary<string, string?>? sessions)
    {
        if (sessions is not { Count: > 0 })
        {
            return null;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (key, id) in sessions)
            {
                if (id is null)
                {
                    writer.WriteNull(key);
                }
                else
                {
                    writer.WriteString(key, id);
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
