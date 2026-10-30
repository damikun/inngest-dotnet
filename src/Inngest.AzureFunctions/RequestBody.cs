// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

namespace Inngest.AzureFunctions;

// The bytes of a request body, and what to dispose once they are no longer needed.
internal readonly struct RequestBody(ReadOnlyMemory<byte> memory, IDisposable? owner) : IDisposable
{
    public ReadOnlyMemory<byte> Memory => memory;

    public void Dispose() => owner?.Dispose();
}
