// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// Implement on a function class to run code when a run of the function is cancelled, by a CancelOn event, a timeout
/// or the REST API. The SDK registers it as a second function, "&lt;function id&gt;-cancel", triggered by
/// inngest/function.cancelled for this function, with one retry.
/// </summary>
public interface ICancelHandler
{
    /// <summary>Runs once after a run of the function was cancelled.</summary>
    Task OnCancelAsync(FunctionContext<FunctionCancelled> context, CancellationToken cancellationToken);
}
