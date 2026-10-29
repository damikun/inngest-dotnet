// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

// Everything Inngest needs to know about a function besides its code. Durations are sent to Inngest as
// duration strings such as "1h30m".
public sealed record FunctionOptions
{
    // Unique within the app. Inngest addresses the function as "<app id>-<id>".
    public required string Id { get; init; }

    public string? Name { get; init; }

    public required IReadOnlyList<FunctionTrigger> Triggers { get; init; }

    // Retries after the first attempt, from 0 to 20. Inngest uses its default when this is null.
    public int? Retries { get; init; }

    public IReadOnlyList<ConcurrencyLimit>? Concurrency { get; init; }

    public Throttle? Throttle { get; init; }

    public RateLimit? RateLimit { get; init; }

    public Debounce? Debounce { get; init; }

    public BatchEvents? BatchEvents { get; init; }

    public Priority? Priority { get; init; }

    // An expression whose value deduplicates runs for 24 hours, for example "event.data.orderId".
    public string? Idempotency { get; init; }

    public IReadOnlyList<CancelOn>? CancelOn { get; init; }

    public FunctionTimeouts? Timeouts { get; init; }

    public Singleton? Singleton { get; init; }

    // Middleware for this function only, run inside the app's middleware in the order listed. It also applies to the
    // function's failure handler. Not part of the sync.
    public IReadOnlyList<FunctionMiddleware>? Middleware { get; init; }

    // Checkpointing for this function, which wins over InngestOptions.Checkpointing. Null uses the app's setting.
    public CheckpointingOptions? Checkpointing { get; init; }
}
