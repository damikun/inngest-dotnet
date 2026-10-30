// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;

namespace Inngest;

/// <summary>An event as a function receives it, with its data deserialized into TData.</summary>
public sealed record InngestEvent<TData>
{
    public required string Name { get; init; }

    public required TData Data { get; init; }

    public string? Id { get; init; }

    public JsonElement? User { get; init; }

    public DateTimeOffset Timestamp { get; init; }

    public string? Version { get; init; }
}
