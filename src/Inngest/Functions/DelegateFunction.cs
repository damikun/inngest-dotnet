// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest.Functions;

internal sealed class DelegateFunction<TEventData>(Func<FunctionContext<TEventData>, CancellationToken, Task<object?>> handler)
    : InngestFunction<TEventData>
{
    public override Task<object?> ExecuteAsync(FunctionContext<TEventData> context, CancellationToken cancellationToken) =>
        handler(context, cancellationToken);
}
