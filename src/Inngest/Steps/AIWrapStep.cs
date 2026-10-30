// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Inngest.Configuration;
using Inngest.Execution;

namespace Inngest.Steps;

// A run step that the Inngest UI shows as a call to an AI model. The input goes into opts.input as a one-element
// array, as inngest-js writes the arguments of step.ai.wrap; an input edited in the UI comes back the same way.
internal sealed class AIWrapStep<TInput, TResult>(string id, TInput input, Func<TInput, CancellationToken, Task<TResult>> handler)
    : FoundStep<TResult>(id)
{
    private static readonly JsonEncodedText InputProperty = JsonEncodedText.Encode("input");
    private static readonly JsonEncodedText TypeProperty = JsonEncodedText.Encode("type");
    private static readonly JsonEncodedText WrapType = JsonEncodedText.Encode("step.ai.wrap");

    public override StepType Type => StepType.AIWrap;

    public override JsonEncodedText Op => Ops.StepPlanned;

    public override bool IsRunnable => true;

    public override string? GetName() => Id;

    // The order of inngest-js: input, then type.
    protected override void WriteOptions(Utf8JsonWriter writer, string? name, InngestSettings settings)
    {
        writer.WritePropertyName(InputProperty);
        if (EditedInput is { } edited)
        {
            edited.WriteTo(writer);
        }
        else
        {
            writer.WriteStartArray();
            JsonSerializer.Serialize(writer, input, InputInfo(settings.SerializerOptions));
            writer.WriteEndArray();
        }

        writer.WriteString(TypeProperty, WrapType);
    }

    public override async Task<object?> ExecuteAsync(FunctionContext context, IServiceProvider services, CancellationToken cancellationToken) =>
        await handler(ReadInput(), cancellationToken).ConfigureAwait(false);

    // Serialized as the declared type, the way the function will read it back.
    public override void WriteOutput(Utf8JsonWriter writer, object? output, JsonSerializerOptions options)
    {
        if (output is TResult value)
        {
            JsonSerializer.Serialize(writer, value, ResultInfo(options));
        }
        else
        {
            base.WriteOutput(writer, output, options);
        }
    }

    protected override TResult Read(JsonElement value, JsonSerializerOptions options) => value.Deserialize(ResultInfo(options))!;

    private TInput ReadInput() =>
        EditedInput is { } edited && edited.GetArrayLength() > 0
            ? edited[0].Deserialize(InputInfo(Owner.SerializerOptions))!
            : input;

    private static JsonTypeInfo<TInput> InputInfo(JsonSerializerOptions options) => (JsonTypeInfo<TInput>)options.GetTypeInfo(typeof(TInput));

    private static JsonTypeInfo<TResult> ResultInfo(JsonSerializerOptions options) => (JsonTypeInfo<TResult>)options.GetTypeInfo(typeof(TResult));
}
