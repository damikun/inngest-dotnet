// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Inngest.UserContext;

// The user object of an event as the app wrote it, with the envelope added.
internal static class EventUsers
{
    // The user with the envelope under the reserved key, which replaces one that was there; every other key is kept.
    // Null when the user is not a JSON object, which the client reports when it sends the event.
    public static JsonElement? WithEnvelope(object? user, string protectedText, JsonSerializerOptions serializer)
    {
        using var existing = Serialize(user, serializer);
        if (existing is not null && existing.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = serializer.Encoder }))
        {
            writer.WriteStartObject();
            if (existing is not null)
            {
                foreach (var property in existing.RootElement.EnumerateObject())
                {
                    if (!property.NameEquals(UserContextCodec.Key))
                    {
                        property.WriteTo(writer);
                    }
                }
            }

            writer.WriteStartObject(UserContextCodec.Key);
            writer.WriteNumber("v"u8, UserContextCodec.Version);
            writer.WriteString("t"u8, protectedText);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        using var merged = JsonDocument.Parse(buffer.WrittenMemory);
        return merged.RootElement.Clone();
    }

    // Null for no user. The JSON types are taken as they are, so that they need no metadata from the app's serializer.
    private static JsonDocument? Serialize(object? user, JsonSerializerOptions serializer) => user switch
    {
        null => null,
        JsonElement element => JsonSerializer.SerializeToDocument(element, serializer.GetTypeInfo(typeof(JsonElement))),
        JsonDocument document => JsonSerializer.SerializeToDocument(document.RootElement, serializer.GetTypeInfo(typeof(JsonElement))),
        JsonNode node => JsonDocument.Parse(node.ToJsonString()),
        _ => JsonSerializer.SerializeToDocument(user, serializer.GetTypeInfo(user.GetType())),
    };
}
