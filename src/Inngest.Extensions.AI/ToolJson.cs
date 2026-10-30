// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Inngest.Extensions.AI;

// The JSON of a tool step. The arguments and the result are what the model and the tool exchange, so they are written
// with the options of the function and of Microsoft.Extensions.AI, like the chat steps are, and the SDK only sees JsonElement.
internal static class ToolJson
{
    // {"name": ..., "callId": ..., "arguments": {...}}: the input the Inngest UI shows, of which an edit keeps the arguments.
    // Null, with the reason, when an argument is not JSON.
    public static JsonElement? TryWriteInput(FunctionInvocationContext context, out string? problem)
    {
        try
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("name", context.Function.Name);
                writer.WriteString("callId", context.CallContent.CallId);
                writer.WritePropertyName("arguments");
                JsonSerializer.Serialize(writer, (IDictionary<string, object?>)context.Arguments, ChatJson.Info<IDictionary<string, object?>>());
                writer.WriteEndObject();
            }

            problem = null;
            return ChatJson.Parse(buffer);
        }
        catch (Exception exception) when (exception is NotSupportedException or JsonException or InvalidOperationException)
        {
            problem = exception.Message;
            return null;
        }
    }

    // The arguments of an input edited in the Inngest UI. The services and context of the call are not JSON and stay.
    public static AIFunctionArguments ReadArguments(JsonElement edited, AIFunctionArguments live)
    {
        if (edited.ValueKind != JsonValueKind.Object
            || !edited.TryGetProperty("arguments", out var argumentsJson)
            || argumentsJson.Deserialize(ChatJson.Info<IDictionary<string, object?>>()) is not { } arguments)
        {
            throw new NonRetriableException("The edited input of a tool call has to be an object with an \"arguments\" object.");
        }

        return new AIFunctionArguments(arguments) { Services = live.Services, Context = live.Context };
    }

    // {"result": ..., "terminate": true}: the result as JSON, and whether the tool asked the function invoking client to stop
    // calling the model, which a replay has to do as the run did. A function made with AIFunctionFactory already returns
    // JsonElement; anything else is written with the options of its function.
    public static JsonElement WriteResult(object? result, bool terminate, JsonSerializerOptions options)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("result");
            switch (result)
            {
                case null:
                case JsonElement { ValueKind: JsonValueKind.Undefined }:
                    writer.WriteNullValue();
                    break;
                case JsonElement element:
                    element.WriteTo(writer);
                    break;
                default:
                    JsonSerializer.Serialize(writer, result, options.GetTypeInfo(result.GetType()));
                    break;
            }

            if (terminate)
            {
                writer.WriteBoolean("terminate", true);
            }

            writer.WriteEndObject();
        }

        return ChatJson.Parse(buffer);
    }

    // The result goes back as the JsonElement a function made with AIFunctionFactory returns, and a result that was null as null.
    public static (object? Result, bool Terminate) ReadResult(JsonElement stored)
    {
        var result = stored.TryGetProperty("result", out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? (object)value
            : null;
        return (result, stored.TryGetProperty("terminate", out var terminate) && terminate.ValueKind == JsonValueKind.True);
    }
}
