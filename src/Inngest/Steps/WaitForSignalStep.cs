// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Inngest.Execution;
using Inngest.Internal;

namespace Inngest.Steps;

// Inngest memoizes {"signal": <name>, "data": <data>} when the signal arrives, or null when the wait timed out.
internal sealed class WaitForSignalStep<TData>(string id, WaitForSignalOptions wait) : FoundStep<InngestSignal<TData>?>(id)
{
    private static readonly JsonEncodedText SignalProperty = JsonEncodedText.Encode("signal");
    private static readonly JsonEncodedText TimeoutProperty = JsonEncodedText.Encode("timeout");
    private static readonly JsonEncodedText ConflictProperty = JsonEncodedText.Encode("conflict");
    private static readonly JsonEncodedText Fail = JsonEncodedText.Encode("fail");
    private static readonly JsonEncodedText Replace = JsonEncodedText.Encode("replace");

    public override StepType Type => StepType.WaitForSignal;

    public override JsonEncodedText Op => Ops.WaitForSignal;

    public override string? GetName() => wait.Signal;

    protected override void WriteOptions(Utf8JsonWriter writer, string? name, InngestSettings settings)
    {
        writer.WriteString(SignalProperty, wait.Signal);
        writer.WriteString(TimeoutProperty, TimeString.Format(wait.Timeout));
        writer.WriteString(ConflictProperty, wait.OnConflict == SignalConflict.Replace ? Replace : Fail);
    }

    protected override InngestSignal<TData>? Read(JsonElement value, JsonSerializerOptions options)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var name = value.TryGetProperty("signal", out var signal) && signal.ValueKind == JsonValueKind.String
            ? signal.GetString()!
            : wait.Signal;

        var data = value.TryGetProperty("data", out var element)
            ? element.Deserialize((JsonTypeInfo<TData>)options.GetTypeInfo(typeof(TData)))
            : default;

        return new InngestSignal<TData>(name, data!);
    }
}
