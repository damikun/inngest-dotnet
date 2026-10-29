// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest.Middleware;

// An immutable list of middleware, shared between the async branches that see it.
internal sealed record ActiveWrap(InngestMiddleware Middleware, ActiveWrap? Next)
{
    public bool Contains(InngestMiddleware candidate)
    {
        for (var node = this; node is not null; node = node.Next)
        {
            if (ReferenceEquals(node.Middleware, candidate))
            {
                return true;
            }
        }

        return false;
    }
}
