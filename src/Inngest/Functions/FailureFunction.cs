// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest.Functions;

// Runs a function's failure handler as an Inngest function of its own.
internal sealed class FailureFunction(Func<FunctionContext<FunctionFailure>, CancellationToken, Task> handler)
    : InngestFunction<FunctionFailure>
{
    public override async Task<object?> ExecuteAsync(FunctionContext<FunctionFailure> context, CancellationToken cancellationToken)
    {
        await handler(context, cancellationToken).ConfigureAwait(false);
        return null;
    }

    public static FunctionOptions CreateOptions(FunctionOptions failed) => new()
    {
        Id = failed.Id + "-failure",
        Name = (failed.Name ?? failed.Id) + " (failure)",
        Triggers = [],
        Retries = 1,
        Middleware = failed.Middleware,
    };
}
