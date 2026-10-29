// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using Inngest.Execution;
using Inngest.Functions;
using Inngest.Internal;

namespace Inngest.Steps;

// Inngest memoizes the matching event as it was received, or null when the wait timed out.
internal sealed class WaitForEventStep<TData>(string id, WaitForEventOptions wait) : FoundStep<InngestEvent<TData>?>(id)
{
    private static readonly JsonEncodedText EventProperty = JsonEncodedText.Encode("event");
    private static readonly JsonEncodedText TimeoutProperty = JsonEncodedText.Encode("timeout");
    private static readonly JsonEncodedText IfProperty = JsonEncodedText.Encode("if");

    public override StepType Type => StepType.WaitForEvent;

    public override JsonEncodedText Op => Ops.WaitForEvent;

    // Older servers read the event name from here when opts.event is missing.
    public override string? GetName() => wait.Event;

    protected override void WriteOptions(Utf8JsonWriter writer, string? name, InngestSettings settings)
    {
        writer.WriteString(EventProperty, wait.Event);
        writer.WriteString(TimeoutProperty, TimeString.Format(wait.Timeout));
        var expression = MatchExpression.Combine(wait.Match, wait.If);
        if (expression is not null)
        {
            writer.WriteString(IfProperty, expression);
        }
    }

    protected override InngestEvent<TData>? Read(JsonElement value, JsonSerializerOptions options) =>
        value.ValueKind == JsonValueKind.Object ? EventReader.Read<TData>(value, options) : null;
}
