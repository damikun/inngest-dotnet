// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inngest.AI;

// The requests and responses of the model helpers, written and read with these contracts rather than the app's
// options: an app under Native AOT has none for them, and a provider must never get a null it would refuse. Only apps
// that use the helpers keep them after trimming.
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AnthropicContentBlock))]
[JsonSerializable(typeof(AnthropicError))]
[JsonSerializable(typeof(AnthropicMessage))]
[JsonSerializable(typeof(AnthropicRequest))]
[JsonSerializable(typeof(AnthropicResponse))]
[JsonSerializable(typeof(AnthropicTool))]
[JsonSerializable(typeof(AnthropicToolChoice))]
[JsonSerializable(typeof(AnthropicUsage))]
[JsonSerializable(typeof(GeminiBlob))]
[JsonSerializable(typeof(GeminiCandidate))]
[JsonSerializable(typeof(GeminiContent))]
[JsonSerializable(typeof(GeminiError))]
[JsonSerializable(typeof(GeminiFunctionCall))]
[JsonSerializable(typeof(GeminiFunctionDeclaration))]
[JsonSerializable(typeof(GeminiFunctionResponse))]
[JsonSerializable(typeof(GeminiGenerationConfig))]
[JsonSerializable(typeof(GeminiPart))]
[JsonSerializable(typeof(GeminiRequest))]
[JsonSerializable(typeof(GeminiResponse))]
[JsonSerializable(typeof(GeminiThinkingConfig))]
[JsonSerializable(typeof(GeminiTool))]
[JsonSerializable(typeof(GeminiUsageMetadata))]
[JsonSerializable(typeof(OpenAIChatChoice))]
[JsonSerializable(typeof(OpenAIChatMessage))]
[JsonSerializable(typeof(OpenAIChatRequest))]
[JsonSerializable(typeof(OpenAIChatResponse))]
[JsonSerializable(typeof(OpenAIChatTool))]
[JsonSerializable(typeof(OpenAIChatUsage))]
[JsonSerializable(typeof(OpenAIError))]
[JsonSerializable(typeof(OpenAIFunctionCall))]
[JsonSerializable(typeof(OpenAIFunctionDefinition))]
[JsonSerializable(typeof(OpenAIInputTokensDetails))]
[JsonSerializable(typeof(OpenAIOutputTokensDetails))]
[JsonSerializable(typeof(OpenAIResponsesContent))]
[JsonSerializable(typeof(OpenAIResponsesInputItem))]
[JsonSerializable(typeof(OpenAIResponsesOutputItem))]
[JsonSerializable(typeof(OpenAIResponsesReasoning))]
[JsonSerializable(typeof(OpenAIResponsesRequest))]
[JsonSerializable(typeof(OpenAIResponsesResponse))]
[JsonSerializable(typeof(OpenAIResponsesTool))]
[JsonSerializable(typeof(OpenAIResponsesUsage))]
[JsonSerializable(typeof(OpenAIToolCall))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class AIJsonContext : JsonSerializerContext
{
}
