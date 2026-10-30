// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;

namespace Inngest.Extensions.AI;

// What a step was made with, as JSON, and a digest of that JSON.
internal readonly record struct RecordedInput(JsonElement Json, string Digest);

// The JSON of a chat step, written and read with the options Microsoft.Extensions.AI ships for its own types: they carry
// the polymorphic AIContent (text, function calls and results, usage, reasoning, ...) and source-generated contracts, so
// the app needs no [JsonSerializable] for any of it and the steps work under Native AOT. The SDK itself only sees
// JsonElement, which it can always read and write.
internal static class ChatJson
{
    // The property of a saved response that is not the response's. Readers of ChatResponse skip it.
    private const string InngestProperty = "inngest";
    private const string InputDigestProperty = "inputDigest";

    private static readonly JsonSerializerOptions Options = AIJsonUtilities.DefaultOptions;

    // {"messages": [...], "options": {...}}: what the Inngest UI shows as the input of the step, and what an edit of it comes
    // back as. Null, with the reason, when the messages or options hold something that is not JSON, such as an additional
    // property of a type the app did not register for Native AOT.
    public static RecordedInput? TryWriteInput(IList<ChatMessage> messages, ChatOptions? options, out string? problem)
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

    public static RecordedInput WriteInput(IList<ChatMessage> messages, ChatOptions? options)
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

        return new RecordedInput(Parse(buffer), Digest(buffer.WrittenSpan));
    }

    // The messages and options of an input edited in the Inngest UI. What JSON cannot hold, the tools of the options and the
    // factory of the provider's own request, stays as the caller passed it. An edit that cannot be read fails the step for
    // good, as retrying it would read it the same way.
    public static (IList<ChatMessage> Messages, ChatOptions? Options) ReadInput(JsonElement edited, ChatOptions? live)
    {
        try
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

            UnwrapPrimitives(messages, options);
            return (messages, options);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new NonRetriableException($"The edited input of a model call could not be read: {exception.Message}", exception);
        }
    }

    // An input is edited when what the engine hands the handler is not the element the step was made with. The engine passes
    // the step's own input on unless Inngest sent an edited one.
    public static bool IsEdited(JsonElement received, JsonElement recorded) => received.GetRawText() != recorded.GetRawText();

    // The response as the result of the step, with the digest of the input the call was made with. A response with
    // additional properties that are not JSON is written without them, as failing the step would make the retry pay for the
    // model again; dropped says why. One that still cannot be written fails the step for good, for the same reason.
    public static JsonElement WriteResponse(ChatResponse response, string? inputDigest, out string? dropped)
    {
        JsonElement written;
        try
        {
            dropped = null;
            written = JsonSerializer.SerializeToElement(response, Info<ChatResponse>());
        }
        catch (Exception exception) when (exception is NotSupportedException or JsonException)
        {
            dropped = exception.Message;
            DropAdditionalProperties(response);
            try
            {
                written = JsonSerializer.SerializeToElement(response, Info<ChatResponse>());
            }
            catch (Exception again) when (again is NotSupportedException or JsonException)
            {
                throw new NonRetriableException($"The response of the model could not be written as JSON: {again.Message}", again);
            }
        }

        return inputDigest is null ? written : WithDigest(written, inputDigest);
    }

    public static ChatResponse ReadResponse(JsonElement json) => json.Deserialize(Info<ChatResponse>()) ?? new ChatResponse();

    // The digest of the input that a saved response was made for; null for a response saved without one, such as a mock's.
    public static string? InputDigestOf(JsonElement saved) =>
        saved.ValueKind == JsonValueKind.Object
        && saved.TryGetProperty(InngestProperty, out var inngest)
        && inngest.ValueKind == JsonValueKind.Object
        && inngest.TryGetProperty(InputDigestProperty, out var digest)
        && digest.ValueKind == JsonValueKind.String
            ? digest.GetString()
            : null;

    public static string Digest(ReadOnlySpan<byte> utf8) => Convert.ToHexString(SHA256.HashData(utf8), 0, 16).ToLowerInvariant();

    public static JsonTypeInfo<T> Info<T>() => (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));

    // The value as JSON on one line, for the places where it is text of its own. The options of Microsoft.Extensions.AI indent.
    public static string Compact(object value, JsonTypeInfo info)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            JsonSerializer.Serialize(writer, value, info);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static JsonElement Parse(ArrayBufferWriter<byte> buffer)
    {
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    // A value that came from JSON is a JsonElement, which a provider that tests for bool or string does not see as one. The
    // primitives go back to the types they were written from; objects and arrays stay JSON.
    public static void UnwrapPrimitives(AdditionalPropertiesDictionary? properties)
    {
        if (properties is null)
        {
            return;
        }

        foreach (var key in properties.Keys.ToArray())
        {
            if (properties[key] is JsonElement element)
            {
                properties[key] = Unwrap(element);
            }
        }
    }

    private static void UnwrapPrimitives(IList<ChatMessage> messages, ChatOptions? options)
    {
        UnwrapPrimitives(options?.AdditionalProperties);
        foreach (var message in messages)
        {
            UnwrapPrimitives(message.AdditionalProperties);
            foreach (var content in message.Contents)
            {
                UnwrapPrimitives(content.AdditionalProperties);
            }
        }
    }

    private static object? Unwrap(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number when element.TryGetDouble(out var number) => number,
        _ => element,
    };

    private static JsonElement WithDigest(JsonElement response, string digest)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in response.EnumerateObject())
            {
                property.WriteTo(writer);
            }

            writer.WriteStartObject(InngestProperty);
            writer.WriteString(InputDigestProperty, digest);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Parse(buffer);
    }

    private static void DropAdditionalProperties(ChatResponse response)
    {
        response.AdditionalProperties = null;
        foreach (var message in response.Messages)
        {
            message.AdditionalProperties = null;
            foreach (var content in message.Contents)
            {
                content.AdditionalProperties = null;
                foreach (var annotation in content.Annotations ?? [])
                {
                    annotation.AdditionalProperties = null;
                }
            }
        }
    }
}
