// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// Limits how many steps of matching runs execute at once. Key is an expression that splits the limit into
/// virtual queues, for example "event.data.accountId".
/// </summary>
public sealed record ConcurrencyLimit(int Limit)
{
    public string? Key { get; init; }

    public ConcurrencyScope Scope { get; init; } = ConcurrencyScope.Function;
}
