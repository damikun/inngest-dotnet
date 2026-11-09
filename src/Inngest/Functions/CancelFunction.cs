// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest.Functions;

// Runs a function's cancel handler as an Inngest function of its own, as FailureFunction runs a failure handler.
internal sealed class CancelFunction(Func<FunctionContext<FunctionCancelled>, CancellationToken, Task> handler)
    : InngestFunction<FunctionCancelled>
{
    public override async Task<object?> ExecuteAsync(FunctionContext<FunctionCancelled> context, CancellationToken cancellationToken)
    {
        await handler(context, cancellationToken).ConfigureAwait(false);
        return null;
    }

    public static FunctionOptions CreateOptions(FunctionOptions cancelled) => new()
    {
        Id = cancelled.Id + "-cancel",
        Name = (cancelled.Name ?? cancelled.Id) + " (cancel)",
        Triggers = [],
        Retries = 1,
        Middleware = cancelled.Middleware,
    };
}
