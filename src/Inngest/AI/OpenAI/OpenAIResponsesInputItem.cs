// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inngest;

/// <summary>An item of the input of the OpenAI Responses API: a message, a function call the model made, or its output.</summary>
public sealed record OpenAIResponsesInputItem
{
    /// <summary>message, function_call or function_call_output; a message may leave it out.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>user, assistant, system or developer, for a message.</summary>
    [JsonPropertyName("role")]
    public string? Role { get; init; }

    /// <summary>The text of a message.</summary>
    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonPropertyName("call_id")]
    public string? CallId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>The arguments of a function call, as JSON text.</summary>
    [JsonPropertyName("arguments")]
    public string? Arguments { get; init; }

    /// <summary>The output of a function call.</summary>
    [JsonPropertyName("output")]
    public string? Output { get; init; }

    /// <summary>Properties this record does not have, sent and read as they are.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }

    /// <summary>A user message.</summary>
    public static OpenAIResponsesInputItem User(string content) => new() { Role = "user", Content = content };

    /// <summary>A developer message, which the Responses API puts above user messages.</summary>
    public static OpenAIResponsesInputItem Developer(string content) => new() { Role = "developer", Content = content };

    /// <summary>An assistant message.</summary>
    public static OpenAIResponsesInputItem Assistant(string content) => new() { Role = "assistant", Content = content };

    /// <summary>A function call the model made, to send back with its output.</summary>
    public static OpenAIResponsesInputItem FunctionCall(string callId, string name, string arguments) =>
        new() { Type = "function_call", CallId = callId, Name = name, Arguments = arguments };

    /// <summary>The output of a function call, for the model's next turn.</summary>
    public static OpenAIResponsesInputItem FunctionCallOutput(string callId, string output) =>
        new() { Type = "function_call_output", CallId = callId, Output = output };
}
