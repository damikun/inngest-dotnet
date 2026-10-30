// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Inngest.Configuration;
using Inngest.Execution;
using Inngest.Internal;

namespace Inngest.Steps;

// Inngest sends the payload to the invoked function as an inngest/function.invoked event and memoizes
// {"data": <its output>}, or {"error": ...} when it fails or the timeout passes.
internal sealed class InvokeStep<TResult>(string id, InvokeOptions invoke) : FoundStep<TResult>(id)
{
    private static readonly JsonEncodedText FunctionIdProperty = JsonEncodedText.Encode("function_id");
    private static readonly JsonEncodedText PayloadProperty = JsonEncodedText.Encode("payload");
    private static readonly JsonEncodedText DataProperty = JsonEncodedText.Encode("data");
    private static readonly JsonEncodedText UserProperty = JsonEncodedText.Encode("user");
    private static readonly JsonEncodedText TimeoutProperty = JsonEncodedText.Encode("timeout");

    public override StepType Type => StepType.Invoke;

    public override JsonEncodedText Op => Ops.InvokeFunction;

    public override string? GetName() => null;

    protected override void WriteOptions(Utf8JsonWriter writer, string? name, InngestSettings settings)
    {
        var appId = string.IsNullOrEmpty(invoke.AppId) ? settings.AppId : invoke.AppId;
        writer.WriteString(FunctionIdProperty, $"{appId}-{invoke.FunctionId}");
        writer.WriteStartObject(PayloadProperty);
        WriteValue(writer, DataProperty, invoke.Data, settings.SerializerOptions);
        WriteValue(writer, UserProperty, invoke.User, settings.SerializerOptions);
        writer.WriteEndObject();
        if (invoke.Timeout is { } timeout)
        {
            writer.WriteString(TimeoutProperty, TimeString.Format(timeout));
        }
    }

    protected override TResult Read(JsonElement value, JsonSerializerOptions options) =>
        value.Deserialize((JsonTypeInfo<TResult>)options.GetTypeInfo(typeof(TResult)))!;

    private static void WriteValue(Utf8JsonWriter writer, JsonEncodedText property, object? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            return;
        }

        writer.WritePropertyName(property);
        JsonSerializer.Serialize(writer, value, options.GetTypeInfo(value.GetType()));
    }
}
