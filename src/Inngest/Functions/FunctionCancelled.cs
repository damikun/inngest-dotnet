// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inngest;

/// <summary>
/// The data of the inngest/function.cancelled event, which Inngest sends when a run is cancelled, for example by a
/// CancelOn event, so that another function can clean up after it.
/// </summary>
public sealed record FunctionCancelled
{
    /// <summary>The cancelled function's composite id, "&lt;app id&gt;-&lt;function id&gt;".</summary>
    [JsonPropertyName("function_id")]
    public required string FunctionId { get; init; }

    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    /// <summary>The event that triggered the cancelled run.</summary>
    [JsonPropertyName("event")]
    public JsonElement Event { get; init; }

    /// <summary>Set when another function started the run with step.invoke.</summary>
    [JsonPropertyName("correlation_id")]
    public string? CorrelationId { get; init; }
}
