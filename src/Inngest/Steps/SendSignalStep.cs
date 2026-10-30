// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using Inngest.Client;
using Inngest.Configuration;
using Inngest.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Steps;

// Sending a signal is a run step whose result is {"runId": <the run it resumed, or null>}, so a replay never sends
// the signal again.
internal sealed class SendSignalStep(string id, InngestSignal signal) : FoundStep<string?>(id)
{
    private const string Name = "sendSignal";

    private static readonly JsonEncodedText TypeProperty = JsonEncodedText.Encode("type");
    private static readonly JsonEncodedText SendSignalType = JsonEncodedText.Encode("step.sendSignal");
    private static readonly JsonEncodedText SignalProperty = JsonEncodedText.Encode("signal");
    private static readonly JsonEncodedText RunIdProperty = JsonEncodedText.Encode("runId");

    public override StepType Type => StepType.SendSignal;

    public override JsonEncodedText Op => Ops.StepPlanned;

    public override bool IsRunnable => true;

    public override string? GetName() => Name;

    protected override void WriteOptions(Utf8JsonWriter writer, string? name, InngestSettings settings)
    {
        writer.WriteString(TypeProperty, SendSignalType);
        writer.WriteString(SignalProperty, signal.Name);
    }

    public override async Task<object?> ExecuteAsync(FunctionContext context, IServiceProvider services, CancellationToken cancellationToken)
    {
        var client = services.GetRequiredService<InngestClient>();
        return await client.SendSignalAsync(signal, cancellationToken).ConfigureAwait(false);
    }

    // Written by hand so that the stored shape does not depend on the app's serializer settings.
    public override void WriteOutput(Utf8JsonWriter writer, object? output, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (output is string runId)
        {
            writer.WriteString(RunIdProperty, runId);
        }
        else
        {
            writer.WriteNull(RunIdProperty);
        }

        writer.WriteEndObject();
    }

    protected override string? Read(JsonElement value, JsonSerializerOptions options) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty("runId", out var runId) && runId.ValueKind == JsonValueKind.String
            ? runId.GetString()
            : null;
}
