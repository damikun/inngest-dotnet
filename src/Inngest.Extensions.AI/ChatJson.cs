// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;

namespace Inngest.Extensions.AI;

// The JSON of a chat step, written and read with the options Microsoft.Extensions.AI ships for its own types: they carry
// the polymorphic AIContent (text, function calls and results, usage, reasoning, ...) and source-generated contracts, so
// the app needs no [JsonSerializable] for any of it and the steps work under Native AOT. The SDK itself only sees
// JsonElement, which it can always read and write.
internal static class ChatJson
{
    private static readonly JsonSerializerOptions Options = AIJsonUtilities.DefaultOptions;

    // {"messages": [...], "options": {...}}: what the Inngest UI shows as the input of the step, and what an edit of it comes
    // back as. Null, with the reason, when the messages or options hold something that is not JSON, such as an additional
    // property of a type the app did not register for Native AOT.
    public static JsonElement? TryWriteInput(IList<ChatMessage> messages, ChatOptions? options, out string? problem)
    {
        try
        {
            problem = null;
            return WriteInput(messages, options);
        }
        catch (Exception exception) when (exception is NotSupportedException or JsonException or InvalidOperationException)
        {
            problem = exception.Message;
            return null;
        }
    }

    public static JsonElement WriteInput(IList<ChatMessage> messages, ChatOptions? options)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("messages");
            JsonSerializer.Serialize(writer, messages, Info<IList<ChatMessage>>());
            if (options is not null)
            {
                writer.WritePropertyName("options");
                JsonSerializer.Serialize(writer, options, Info<ChatOptions>());
            }

            writer.WriteEndObject();
        }

        return Parse(buffer);
    }

    // The messages and options of an input edited in the Inngest UI. What JSON cannot hold, the tools of the options and the
    // factory of the provider's own request, stays as the caller passed it.
    public static (IList<ChatMessage> Messages, ChatOptions? Options) ReadInput(JsonElement edited, ChatOptions? live)
    {
        if (edited.ValueKind != JsonValueKind.Object
            || !edited.TryGetProperty("messages", out var messagesJson)
            || messagesJson.Deserialize(Info<IList<ChatMessage>>()) is not { } messages)
        {
            throw new NonRetriableException("The edited input of a model call has to be an object with a \"messages\" array.");
        }

        ChatOptions? options = null;
        if (edited.TryGetProperty("options", out var optionsJson) && optionsJson.ValueKind == JsonValueKind.Object)
        {
            options = optionsJson.Deserialize(Info<ChatOptions>());
        }

        if (live is not null)
        {
            options ??= new ChatOptions();
            options.Tools = live.Tools;
            options.RawRepresentationFactory = live.RawRepresentationFactory;
        }

        return (messages, options);
    }

    // An input is edited when what the engine hands the handler is not the element the step was made with. The engine passes
    // the step's own input on unless Inngest sent an edited one.
    public static bool IsEdited(JsonElement received, JsonElement recorded) => received.GetRawText() != recorded.GetRawText();

    public static JsonElement WriteResponse(ChatResponse response) => JsonSerializer.SerializeToElement(response, Info<ChatResponse>());

    public static ChatResponse ReadResponse(JsonElement json) => json.Deserialize(Info<ChatResponse>()) ?? new ChatResponse();

    public static JsonTypeInfo<T> Info<T>() => (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));

    public static JsonElement Parse(ArrayBufferWriter<byte> buffer)
    {
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
