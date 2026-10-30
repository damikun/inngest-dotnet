// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Inngest.Configuration;
using Inngest.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Realtime;

// Publishes in the SDK, as a run step does, tagged with the run; the data it returns is memoized, so a replay does not
// publish again.
internal sealed class RealtimePublishStep<T>(string id, RealtimeTopic topic, T data) : FoundStep<T>(id)
{
    private static readonly JsonEncodedText TypeProperty = JsonEncodedText.Encode("type");
    private static readonly JsonEncodedText PublishType = JsonEncodedText.Encode("step.realtime.publish");

    public override StepType Type => StepType.RealtimePublish;

    public override JsonEncodedText Op => Ops.StepPlanned;

    public override bool IsRunnable => true;

    public override string? GetName() => Id;

    protected override void WriteOptions(Utf8JsonWriter writer, string? name, InngestSettings settings) =>
        writer.WriteString(TypeProperty, PublishType);

    public override async Task<object?> ExecuteAsync(FunctionContext context, IServiceProvider services, CancellationToken cancellationToken)
    {
        var body = RealtimePayload.Body(data, Owner.SerializerOptions);
        await services.GetRequiredService<IRealtimeApi>().PublishAsync(topic, context.RunId, body, cancellationToken).ConfigureAwait(false);
        return data;
    }

    // Serialized as the declared type, the way the function will read it back.
    public override void WriteOutput(Utf8JsonWriter writer, object? output, JsonSerializerOptions options)
    {
        if (output is T value)
        {
            JsonSerializer.Serialize(writer, value, TypeInfo(options));
        }
        else
        {
            base.WriteOutput(writer, output, options);
        }
    }

    protected override T Read(JsonElement value, JsonSerializerOptions options) => value.Deserialize(TypeInfo(options))!;

    private static JsonTypeInfo<T> TypeInfo(JsonSerializerOptions options) => (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
}
