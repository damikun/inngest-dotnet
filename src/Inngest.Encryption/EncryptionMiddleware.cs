// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;

namespace Inngest.Encryption;

// What @inngest/middleware-encryption does with transformFunctionInput, wrapStep, wrapStepHandler, wrapFunctionHandler
// and transformSendEvent, as payload transforms on the raw JSON and a wrapper around event sends.
internal sealed class EncryptionMiddleware(PayloadEncryptor encryptor) : InngestMiddleware, IPayloadTransform
{
    public bool TransformEvent(JsonElement @event, Utf8JsonWriter writer) => encryptor.DecryptEvent(@event, writer);

    // The event a wait matched is the one case where a result is an event rather than a value; the TypeScript package
    // returns it as it was sent, with the envelope in its data.
    public bool TransformStepResult(FunctionContext context, StepInfo step, JsonElement result, Utf8JsonWriter writer) =>
        step.Type == StepType.WaitForEvent ? encryptor.DecryptEvent(result, writer) : encryptor.DecryptValue(result, writer);

    public bool TransformStepOutput(FunctionContext context, StepInfo step, JsonElement output, Utf8JsonWriter writer) =>
        encryptor.EncryptOutput(output, writer);

    public bool TransformFunctionOutput(FunctionContext context, JsonElement output, Utf8JsonWriter writer) =>
        encryptor.EncryptOutput(output, writer);

    public override Task<SendEventsResult> WrapSendEventAsync(
        FunctionContext? context,
        IReadOnlyList<InngestEvent> events,
        Func<IReadOnlyList<InngestEvent>, Task<SendEventsResult>> next,
        CancellationToken cancellationToken) => next(encryptor.EncryptEvents(events));
}
