// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

// Id is what Inngest memoizes the step by, so it has to stay the same; Name is only what the Inngest UI shows and
// can change at any time. ParallelMode applies to this step instead of the mode of an enclosing ParallelAsync.
public sealed record StepOptions(string Id)
{
    public string? Name { get; init; }

    public ParallelMode? ParallelMode { get; init; }

    public static implicit operator StepOptions(string id) => new(id);
}

// Match is a property path that must be equal on the triggering event and the awaited one, such as
// "data.orderId". If is a full expression where "event" is the triggering event and "async" the awaited one.
public sealed record WaitForEventOptions(string Event, TimeSpan Timeout)
{
    public string? Match { get; init; }

    public string? If { get; init; }
}

// FunctionId is the id the function was registered with inside AppId, which defaults to the current app.
public sealed record InvokeOptions(string FunctionId)
{
    public string? AppId { get; init; }

    public object? Data { get; init; }

    public object? User { get; init; }

    // How long to wait for the invoked function to finish before the step fails.
    public TimeSpan? Timeout { get; init; }
}
