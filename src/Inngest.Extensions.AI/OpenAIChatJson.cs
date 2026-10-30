// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Inngest.Extensions.AI;

// Messages and options as the request body of the OpenAI chat completions API, which Inngest sends to the provider for
// an inference step, and its response as a ChatResponse. Written and read by hand, so that it needs no reflection.
internal static class OpenAIChatJson
{
    private const string RefusalCode = "Refusal";

    public static JsonElement WriteRequest(string model, IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", options?.ModelId ?? model);
            writer.WritePropertyName("messages");
            writer.WriteStartArray();
            if (options?.Instructions is { Length: > 0 } instructions)
            {
                WriteTextMessage(writer, "system", instructions);
            }

            foreach (var message in messages)
            {
                WriteMessage(writer, message);
            }

            writer.WriteEndArray();
            WriteOptions(writer, options);
            writer.WriteEndObject();
        }

        return ChatJson.Parse(buffer);
    }

    // The answer of the provider as a response. One that is an error, or has no choices, is no response, and is the same on
    // every replay, so it fails for good.
    public static ChatResponse ReadResponse(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object)
        {
            throw new NonRetriableException($"The provider answered with {Excerpt(json)}, not with a chat completion.");
        }

        if (json.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            throw new NonRetriableException($"The provider answered with an error: {Describe(error)}");
        }

        if (!json.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
        {
            throw new NonRetriableException($"The provider answered without choices: {Excerpt(json)}");
        }

        var response = new ChatResponse();
        response.ResponseId = String(json, "id");
        response.ModelId = String(json, "model");
        if (json.TryGetProperty("created", out var created) && created.ValueKind == JsonValueKind.Number && created.TryGetInt64(out var seconds))
        {
            response.CreatedAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
        }

        var choice = choices[0];
        response.Messages.Add(ReadMessage(choice.TryGetProperty("message", out var message) ? message : default));
        response.FinishReason = String(choice, "finish_reason") switch
        {
            "stop" => ChatFinishReason.Stop,
            "length" => ChatFinishReason.Length,
            "tool_calls" or "function_call" => ChatFinishReason.ToolCalls,
            "content_filter" => ChatFinishReason.ContentFilter,
            { } other => new ChatFinishReason(other),
            null => null,
        };

        if (json.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            response.Usage = new UsageDetails
            {
                InputTokenCount = Count(usage, "prompt_tokens"),
                OutputTokenCount = Count(usage, "completion_tokens"),
                TotalTokenCount = Count(usage, "total_tokens"),
            };
        }

        return response;
    }

    private static void WriteMessage(Utf8JsonWriter writer, ChatMessage message)
    {
        if (message.Role == ChatRole.Tool)
        {
            foreach (var result in message.Contents.OfType<FunctionResultContent>())
            {
                writer.WriteStartObject();
                writer.WriteString("role", "tool");
                writer.WriteString("tool_call_id", result.CallId);
                writer.WriteString("content", ResultText(result.Result));
                writer.WriteEndObject();
            }

            return;
        }

        var role = message.Role == ChatRole.Assistant ? "assistant" : message.Role == ChatRole.System ? "system" : message.Role == ChatRole.User ? "user" : message.Role.Value;
        writer.WriteStartObject();
        writer.WriteString("role", role);
        var calls = message.Contents.OfType<FunctionCallContent>().ToArray();
        var images = message.Contents.Where(content => content is UriContent uri && uri.HasTopLevelMediaType("image") || content is DataContent data && data.HasTopLevelMediaType("image")).ToArray();
        var text = string.Concat(message.Contents.OfType<TextContent>().Select(content => content.Text));
        var refusal = message.Role == ChatRole.Assistant ? message.Contents.OfType<ErrorContent>().FirstOrDefault(content => content.ErrorCode == RefusalCode)?.Message : null;
        if (images.Length > 0)
        {
            writer.WriteStartArray("content");
            if (text.Length > 0)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", text);
                writer.WriteEndObject();
            }

            foreach (var image in images)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "image_url");
                writer.WriteStartObject("image_url");
                writer.WriteString("url", image is UriContent uri ? uri.Uri.AbsoluteUri : ((DataContent)image).Uri);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }
        else if (text.Length > 0 || (calls.Length == 0 && refusal is null))
        {
            writer.WriteString("content", text);
        }
        else
        {
            writer.WriteNull("content");
        }

        if (refusal is not null)
        {
            writer.WriteString("refusal", refusal);
        }

        if (calls.Length > 0)
        {
            writer.WriteStartArray("tool_calls");
            foreach (var call in calls)
            {
                writer.WriteStartObject();
                writer.WriteString("id", call.CallId);
                writer.WriteString("type", "function");
                writer.WriteStartObject("function");
                writer.WriteString("name", call.Name);

                // The API takes the arguments as a string of JSON.
                writer.WriteString("arguments", call.Arguments is null ? "{}" : ChatJson.Compact(call.Arguments, ChatJson.Info<IDictionary<string, object?>>()));
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static void WriteTextMessage(Utf8JsonWriter writer, string role, string text)
    {
        writer.WriteStartObject();
        writer.WriteString("role", role);
        writer.WriteString("content", text);
        writer.WriteEndObject();
    }

    private static void WriteOptions(Utf8JsonWriter writer, ChatOptions? options)
    {
        if (options is null)
        {
            return;
        }

        WriteNumber(writer, "temperature", options.Temperature);
        WriteNumber(writer, "top_p", options.TopP);
        WriteNumber(writer, "frequency_penalty", options.FrequencyPenalty);
        WriteNumber(writer, "presence_penalty", options.PresencePenalty);
        if (options.MaxOutputTokens is { } maxTokens)
        {
            writer.WriteNumber("max_completion_tokens", maxTokens);
        }

        if (options.Seed is { } seed)
        {
            writer.WriteNumber("seed", seed);
        }

        if (options.StopSequences is { Count: > 0 } stops)
        {
            writer.WriteStartArray("stop");
            foreach (var stop in stops)
            {
                writer.WriteStringValue(stop);
            }

            writer.WriteEndArray();
        }

        // Strict mode is the caller's to ask for, with the "strict" additional property, as for Microsoft's OpenAI adapter. Left
        // out, it is left out of the request too; asked for, the schemas are changed into the dialect it takes.
        var strict = HasStrict(options.AdditionalProperties);
        if (options.ResponseFormat is ChatResponseFormatJson format)
        {
            if ((strict is true ? StrictSchemas.Of(format) : format.Schema) is { } schema)
            {
                writer.WriteStartObject("response_format");
                writer.WriteString("type", "json_schema");
                writer.WriteStartObject("json_schema");
                writer.WriteString("name", format.SchemaName ?? "json_schema");
                if (format.SchemaDescription is { } schemaDescription)
                {
                    writer.WriteString("description", schemaDescription);
                }

                writer.WritePropertyName("schema");
                schema.WriteTo(writer);
                if (strict is { } strictSchema)
                {
                    writer.WriteBoolean("strict", strictSchema);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteStartObject("response_format");
                writer.WriteString("type", "json_object");
                writer.WriteEndObject();
            }
        }

        var functions = options.Tools?.OfType<AIFunctionDeclaration>().ToArray() ?? [];
        if (functions.Length > 0)
        {
            writer.WriteStartArray("tools");
            foreach (var function in functions)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "function");
                writer.WriteStartObject("function");
                writer.WriteString("name", function.Name);
                writer.WriteString("description", function.Description);
                writer.WritePropertyName("parameters");
                var functionStrict = HasStrict(function.AdditionalProperties) ?? strict;
                (functionStrict is true ? StrictSchemas.Of(function) : function.JsonSchema).WriteTo(writer);
                if (functionStrict is { } strictFunction)
                {
                    writer.WriteBoolean("strict", strictFunction);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            if (options.AllowMultipleToolCalls is { } allowMultiple)
            {
                writer.WriteBoolean("parallel_tool_calls", allowMultiple);
            }

            switch (options.ToolMode)
            {
                case NoneChatToolMode:
                    writer.WriteString("tool_choice", "none");
                    break;
                case RequiredChatToolMode { RequiredFunctionName: { } name }:
                    writer.WriteStartObject("tool_choice");
                    writer.WriteString("type", "function");
                    writer.WriteStartObject("function");
                    writer.WriteString("name", name);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    break;
                case RequiredChatToolMode:
                    writer.WriteString("tool_choice", "required");
                    break;
                case AutoChatToolMode:
                    writer.WriteString("tool_choice", "auto");
                    break;
            }
        }
    }

    private static bool? HasStrict(IReadOnlyDictionary<string, object?>? properties) =>
        properties is not null && properties.TryGetValue("strict", out var value) && value is bool strict ? strict : null;

    private static void WriteNumber(Utf8JsonWriter writer, string name, float? value)
    {
        if (value is { } number)
        {
            // As the text of the shortest round trip, so that 0.2f is 0.2 and not 0.20000000298023224.
            writer.WritePropertyName(name);
            writer.WriteRawValue(number.ToString("R", CultureInfo.InvariantCulture), skipInputValidation: false);
        }
    }

    private static string ResultText(object? result) => result switch
    {
        null => string.Empty,
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
        JsonElement element => element.GetRawText(),
        _ => ChatJson.Compact(result, AIJsonUtilities.DefaultOptions.GetTypeInfo(result.GetType())),
    };

    private static ChatMessage ReadMessage(JsonElement message)
    {
        var chat = new ChatMessage { Role = ChatRole.Assistant };
        if (message.ValueKind != JsonValueKind.Object)
        {
            return chat;
        }

        if (String(message, "content") is { Length: > 0 } text)
        {
            chat.Contents.Add(new TextContent(text));
        }

        // As Microsoft's OpenAI adapter hands it on: a refusal is an error content with this code.
        if (String(message, "refusal") is { Length: > 0 } refusal)
        {
            chat.Contents.Add(new ErrorContent(refusal) { ErrorCode = RefusalCode });
        }

        if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in calls.EnumerateArray())
            {
                if (!call.TryGetProperty("function", out var function))
                {
                    continue;
                }

                var content = new FunctionCallContent(String(call, "id") ?? string.Empty, String(function, "name") ?? string.Empty);
                try
                {
                    content.Arguments = String(function, "arguments") is { Length: > 0 } arguments
                        ? JsonSerializer.Deserialize(arguments, ChatJson.Info<IDictionary<string, object?>>())
                        : new Dictionary<string, object?>();
                }
                catch (JsonException exception)
                {
                    // The model wrote arguments that are not JSON, which FunctionInvokingChatClient reports back to it.
                    content.Exception = exception;
                }

                chat.Contents.Add(content);
            }
        }

        return chat;
    }

    private static string Describe(JsonElement error) => error.ValueKind switch
    {
        JsonValueKind.String => error.GetString() ?? string.Empty,
        JsonValueKind.Object when String(error, "message") is { } message => message,
        _ => Excerpt(error),
    };

    private static string Excerpt(JsonElement element)
    {
        var text = element.GetRawText();
        return text.Length <= 200 ? text : $"{text[..200]}...";
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Count(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count) ? count : null;
}
