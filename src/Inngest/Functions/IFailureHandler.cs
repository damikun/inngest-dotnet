// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

// Implement on a function class to run code once the function has failed all of its attempts. The SDK registers
// it as a second function, "<function id>-failure", triggered by inngest/function.failed for this function.
public interface IFailureHandler
{
    /// <summary>Runs once after the function has failed all of its attempts.</summary>
    Task OnFailureAsync(FunctionContext<FunctionFailure> context, CancellationToken cancellationToken);
}
