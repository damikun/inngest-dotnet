// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inngest;

// The data of the inngest/function.failed event that a failure handler runs with.
public sealed record FunctionFailure
{
    // The failed function's composite id, "<app id>-<function id>".
    [JsonPropertyName("function_id")]
    public required string FunctionId { get; init; }

    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    [JsonPropertyName("error")]
    public required FunctionError Error { get; init; }

    // The event that triggered the failed run.
    [JsonPropertyName("event")]
    public JsonElement Event { get; init; }
}

// An error as Inngest stores it: the exception type name, its message, the stack trace if one was captured and the
// error that caused it.
public sealed record FunctionError
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "Error";

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("stack")]
    public string? Stack { get; init; }

    // What an inner exception becomes on the wire. Inngest keeps up to five levels.
    [JsonPropertyName("cause")]
    public FunctionError? Cause { get; init; }
}
