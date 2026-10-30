// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inngest;

/// <summary>
/// The data of the inngest/function.finished event, which Inngest sends when a run ends, however it ends. A run that
/// failed has an Error, and Inngest then also puts the error the SDK returned in Result. A run that completed has only
/// its output in Result, and a cancelled run has neither.
/// </summary>
public sealed record FunctionFinished
{
    /// <summary>The finished function's composite id, "&lt;app id&gt;-&lt;function id&gt;".</summary>
    [JsonPropertyName("function_id")]
    public required string FunctionId { get; init; }

    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    /// <summary>
    /// The output of the run's last call, as JSON. Undefined when there was none, as for a cancelled run.
    /// </summary>
    [JsonPropertyName("result")]
    public JsonElement Result { get; init; }

    [JsonPropertyName("error")]
    public FunctionError? Error { get; init; }

    /// <summary>The event that triggered the run.</summary>
    [JsonPropertyName("event")]
    public JsonElement Event { get; init; }

    /// <summary>Set when another function started the run with step.invoke.</summary>
    [JsonPropertyName("correlation_id")]
    public string? CorrelationId { get; init; }
}
