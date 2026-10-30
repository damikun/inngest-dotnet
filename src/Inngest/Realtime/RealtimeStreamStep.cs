// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using Inngest.Configuration;
using Inngest.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Realtime;

// Streams in the SDK, as a run step runs, and memoizes the text it streamed, so a replay neither streams again nor
// enumerates the chunks, which would call whatever produces them. inngest-js publishes a stream with the same step type.
internal sealed class RealtimeStreamStep(string id, RealtimeTopic topic, IAsyncEnumerable<string> chunks) : FoundStep<string>(id)
{
    private static readonly JsonEncodedText TypeProperty = JsonEncodedText.Encode("type");
    private static readonly JsonEncodedText PublishType = JsonEncodedText.Encode("step.realtime.publish");

    public override StepType Type => StepType.RealtimePublish;

    public override JsonEncodedText Op => Ops.StepPlanned;

    public override bool IsRunnable => true;

    public override string? GetName() => Id;

    protected override void WriteOptions(Utf8JsonWriter writer, string? name, InngestSettings settings) =>
        writer.WriteString(TypeProperty, PublishType);

    public override async Task<object?> ExecuteAsync(FunctionContext context, IServiceProvider services, CancellationToken cancellationToken) =>
        await services.GetRequiredService<IRealtimeApi>().PublishStreamAsync(topic, context.RunId, chunks, cancellationToken).ConfigureAwait(false);

    public override void WriteOutput(Utf8JsonWriter writer, object? output, JsonSerializerOptions options)
    {
        if (output is string text)
        {
            writer.WriteStringValue(text);
        }
        else
        {
            base.WriteOutput(writer, output, options);
        }
    }

    protected override string Read(JsonElement value, JsonSerializerOptions options) => value.GetString() ?? string.Empty;
}
