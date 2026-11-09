// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// Implement on an exception to give Inngest a code for it, such as "card_declined". The code is written as the
/// error's "code" field, which the TypeScript SDK reads as the error's code too, and comes back as
/// <see cref="FunctionError.Code"/> on <see cref="StepFailedException.Error"/> and in failure handlers. An exception
/// that does not implement this can give its code as a "code" entry of <see cref="Exception.Data"/>.
/// </summary>
public interface IHasErrorCode
{
    /// <summary>The error's code, or null when it has none.</summary>
    string? Code { get; }
}
