// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Inngest.Configuration;
using Inngest.Events;
using Inngest.Execution;
using Inngest.Internal;

namespace Inngest.Steps;

// Inngest sends the payload to the invoked function as an inngest/function.invoked event and memoizes
// {"data": <its output>}, or {"error": ...} when it fails or the timeout passes. When the invoked run is cancelled,
// Inngest memoizes the whole inngest/function.finished event instead, which would otherwise be read as the output.
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
        var meta = settings.SessionPropagation ? EventSessions.Inherit(invoke.Meta, Owner.Context.Sessions) : invoke.Meta;
        EventSessions.WriteMeta(writer, meta, $"invoke step \"{Id}\"");
        writer.WriteEndObject();
        if (invoke.Timeout is { } timeout)
        {
            writer.WriteString(TimeoutProperty, TimeString.Format(timeout));
        }
    }

    protected override TResult Read(JsonElement value, JsonSerializerOptions options)
    {
        if (IsCancelledRun(value, out var functionId))
        {
            throw Cancelled(functionId ?? invoke.FunctionId);
        }

        return value.Deserialize((JsonTypeInfo<TResult>)options.GetTypeInfo(typeof(TResult)))!;
    }

    // Like a failed attempt that no retry can fix: the call site gets a StepFailedException it can catch.
    private StepFailedException Cancelled(string functionId) =>
        new(FinalId, new FunctionError { Name = nameof(NonRetriableException), Message = $"The invoked function \"{functionId}\" was cancelled." });

    // The finished event of a cancelled run carries data._inngest.status "Cancelled"; its data alone counts too.
    private static bool IsCancelledRun(JsonElement value, out string? functionId)
    {
        functionId = null;
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var data = value;
        if (value.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && name.ValueEquals("inngest/function.finished"))
        {
            if (!value.TryGetProperty("data", out data) || data.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
        }
        else if (!value.TryGetProperty("function_id", out _))
        {
            return false;
        }

        if (!data.TryGetProperty("_inngest", out var inngest)
            || inngest.ValueKind != JsonValueKind.Object
            || !inngest.TryGetProperty("status", out var status)
            || status.ValueKind != JsonValueKind.String
            || !status.ValueEquals("Cancelled"))
        {
            return false;
        }

        if (data.TryGetProperty("function_id", out var id) && id.ValueKind == JsonValueKind.String)
        {
            functionId = id.GetString();
        }

        return true;
    }

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
