// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inngest;

/// <summary>The data of the inngest/function.failed event that a failure handler runs with.</summary>
public sealed record FunctionFailure
{
    /// <summary>The failed function's composite id, "&lt;app id&gt;-&lt;function id&gt;".</summary>
    [JsonPropertyName("function_id")]
    public required string FunctionId { get; init; }

    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    [JsonPropertyName("error")]
    public required FunctionError Error { get; init; }

    /// <summary>The event that triggered the failed run.</summary>
    [JsonPropertyName("event")]
    public JsonElement Event { get; init; }
}
