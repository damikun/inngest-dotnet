// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using Inngest.Client;
using Inngest.Configuration;
using Inngest.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Steps;

// Sending events is a run step whose result is {"ids": [...]}, so a replay never sends the events again.
internal sealed class SendEventStep(string id, IReadOnlyList<InngestEvent> events) : FoundStep<SendEventsResult>(id)
{
    private const string Name = "sendEvent";

    private static readonly JsonEncodedText TypeProperty = JsonEncodedText.Encode("type");
    private static readonly JsonEncodedText SendEventType = JsonEncodedText.Encode("step.sendEvent");
    private static readonly JsonEncodedText IdsProperty = JsonEncodedText.Encode("ids");

    public override StepType Type => StepType.SendEvent;

    public override JsonEncodedText Op => Ops.StepPlanned;

    public override bool IsRunnable => true;

    public override string? GetName() => Name;

    protected override void WriteOptions(Utf8JsonWriter writer, string? name, InngestSettings settings) =>
        writer.WriteString(TypeProperty, SendEventType);

    public override async Task<object?> ExecuteAsync(FunctionContext context, IServiceProvider services, CancellationToken cancellationToken)
    {
        var client = services.GetRequiredService<InngestClient>();
        return await client.SendAsync(events, context, services, cancellationToken).ConfigureAwait(false);
    }

    // Written by hand so that the stored shape does not depend on the app's serializer settings.
    public override void WriteOutput(Utf8JsonWriter writer, object? output, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteStartArray(IdsProperty);
        if (output is SendEventsResult result)
        {
            foreach (var eventId in result.Ids)
            {
                writer.WriteStringValue(eventId);
            }
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    protected override SendEventsResult Read(JsonElement value, JsonSerializerOptions options)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("ids", out var ids) ||
            ids.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"The stored result of step \"{FinalId}\" has no \"ids\" array.");
        }

        var list = new string[ids.GetArrayLength()];
        var index = 0;
        foreach (var eventId in ids.EnumerateArray())
        {
            list[index++] = eventId.GetString() ?? string.Empty;
        }

        return new SendEventsResult(list);
    }
}
