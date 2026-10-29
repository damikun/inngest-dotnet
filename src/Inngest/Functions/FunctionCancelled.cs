// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inngest;

// The data of the inngest/function.cancelled event, which Inngest sends when a run is cancelled, for example by a
// CancelOn event, so that another function can clean up after it.
public sealed record FunctionCancelled
{
    // The cancelled function's composite id, "<app id>-<function id>".
    [JsonPropertyName("function_id")]
    public required string FunctionId { get; init; }

    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    // The event that triggered the cancelled run.
    [JsonPropertyName("event")]
    public JsonElement Event { get; init; }

    // Set when another function started the run with step.invoke.
    [JsonPropertyName("correlation_id")]
    public string? CorrelationId { get; init; }
}
