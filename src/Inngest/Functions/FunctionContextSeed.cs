// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Inngest.Functions;

// Untyped inputs from which a function builds its typed context. Event and Events are the raw JSON objects
// from the request; Events is empty when the request carries only Event.
internal sealed record FunctionContextSeed(
    string FunctionId,
    string RunId,
    int Attempt,
    int? MaxAttempts,
    JsonElement Event,
    IReadOnlyList<JsonElement> Events,
    IStepTools Step,
    ILogger Logger,
    IServiceProvider Services,
    JsonSerializerOptions SerializerOptions,
    CancellationToken CancellationToken)
{
    public string? RequestId { get; init; }

    public string? JobId { get; init; }

    // Whether the run's sessions pass on to what it sends; FunctionContext.Sessions stays empty otherwise.
    public bool PropagateSessions { get; init; }

    // The middleware of the call, the function's own included.
    public IReadOnlyList<InngestMiddleware>? Middleware { get; init; }

    // Rewrite the events before they are read; null when no middleware of the call transforms payloads.
    public IPayloadTransform[]? Transforms { get; init; }
}
