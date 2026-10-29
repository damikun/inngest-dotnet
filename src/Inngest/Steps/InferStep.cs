// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Inngest.Execution;
using Inngest.Internal;

namespace Inngest.Steps;

// Inngest sends the body to the provider and memoizes {"data": <the provider's response>}. A response with an error
// status is retried like a failed step and memoized as {"error": ...} once the retries are used up.
internal sealed class InferStep<TResponse>(string id, InferOptions infer) : FoundStep<TResponse>(id)
{
    private static readonly JsonEncodedText TypeProperty = JsonEncodedText.Encode("type");
    private static readonly JsonEncodedText InferType = JsonEncodedText.Encode("step.ai.infer");
    private static readonly JsonEncodedText UrlProperty = JsonEncodedText.Encode("url");
    private static readonly JsonEncodedText HeadersProperty = JsonEncodedText.Encode("headers");
    private static readonly JsonEncodedText AuthKeyProperty = JsonEncodedText.Encode("auth_key");
    private static readonly JsonEncodedText FormatProperty = JsonEncodedText.Encode("format");
    private static readonly JsonEncodedText BodyProperty = JsonEncodedText.Encode("body");

    public override StepType Type => StepType.AIInfer;

    public override JsonEncodedText Op => Ops.AIGateway;

    public override string? GetName() => null;

    private static string FormatName(AIFormat format) => format switch
    {
        AIFormat.OpenAIChat => "openai-chat",
        AIFormat.OpenAIResponses => "openai-responses",
        AIFormat.Anthropic => "anthropic",
        AIFormat.Gemini => "gemini",
        AIFormat.Grok => "grok",
        AIFormat.AzureOpenAI => "azure-openai",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown AI format."),
    };

    // The order of inngest-js: type, url, headers, auth_key, format, body.
    protected override void WriteOptions(Utf8JsonWriter writer, string? name, InngestSettings settings)
    {
        writer.WriteString(TypeProperty, InferType);
        writer.WriteString(UrlProperty, infer.Url.AbsoluteUri);
        if (infer.Headers is { } headers)
        {
            writer.WriteStartObject(HeadersProperty);
            foreach (var (header, value) in headers)
            {
                writer.WriteString(header, value);
            }

            writer.WriteEndObject();
        }

        writer.WriteString(AuthKeyProperty, infer.AuthKey ?? string.Empty);
        writer.WriteString(FormatProperty, FormatName(infer.Format));
        writer.WritePropertyName(BodyProperty);
        JsonSerializer.Serialize(writer, infer.Body, settings.SerializerOptions.GetTypeInfo(infer.Body.GetType()));
    }

    protected override TResponse Read(JsonElement value, JsonSerializerOptions options) =>
        value.Deserialize((JsonTypeInfo<TResponse>)options.GetTypeInfo(typeof(TResponse)))!;
}
