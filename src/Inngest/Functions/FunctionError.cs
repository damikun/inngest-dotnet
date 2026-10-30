// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json.Serialization;

namespace Inngest;

/// <summary>
/// An error as Inngest stores it: the exception type name, its message, the stack trace if one was captured and the
/// error that caused it.
/// </summary>
public sealed record FunctionError
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "Error";

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("stack")]
    public string? Stack { get; init; }

    /// <summary>What an inner exception becomes on the wire. Inngest keeps up to five levels.</summary>
    [JsonPropertyName("cause")]
    public FunctionError? Cause { get; init; }
}
