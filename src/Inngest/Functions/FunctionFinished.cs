// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inngest;

// The data of the inngest/function.finished event, which Inngest sends when a run ends, however it ends. A run that
// failed has an Error, and Inngest then also puts the error the SDK returned in Result. A run that completed has only
// its output in Result, and a cancelled run has neither.
public sealed record FunctionFinished
{
    // The finished function's composite id, "<app id>-<function id>".
    [JsonPropertyName("function_id")]
    public required string FunctionId { get; init; }

    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    // The output of the run's last call, as JSON. Undefined when there was none, as for a cancelled run.
    [JsonPropertyName("result")]
    public JsonElement Result { get; init; }

    [JsonPropertyName("error")]
    public FunctionError? Error { get; init; }

    // The event that triggered the run.
    [JsonPropertyName("event")]
    public JsonElement Event { get; init; }

    // Set when another function started the run with step.invoke.
    [JsonPropertyName("correlation_id")]
    public string? CorrelationId { get; init; }
}
