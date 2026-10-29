// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Inngest.Functions;
using Inngest.Internal;

namespace Inngest.Registration;

internal interface IFunctionRegistry
{
    IReadOnlyList<FunctionDefinition> Functions { get; }

    // Resolves the fnId query parameter, which is "<app id>-<function id>".
    bool TryGet(string compositeId, out FunctionDefinition function);

    // Writes the sync body Inngest expects, with every function's step URL pointing at serveUrl.
    void WriteRegistration(PooledBufferWriter writer, Uri serveUrl, string framework, string? deployType = null);
}
