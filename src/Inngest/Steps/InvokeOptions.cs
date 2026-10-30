// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// FunctionId is the id the function was registered with inside AppId, which defaults to the current app.
/// </summary>
public sealed record InvokeOptions(string FunctionId)
{
    public string? AppId { get; init; }

    public object? Data { get; init; }

    public object? User { get; init; }

    /// <summary>How long to wait for the invoked function to finish before the step fails.</summary>
    public TimeSpan? Timeout { get; init; }
}
