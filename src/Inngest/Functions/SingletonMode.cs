// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

public enum SingletonMode
{
    /// <summary>A new run is skipped while another run with the same key is active.</summary>
    Skip,

    /// <summary>A new run cancels the active run with the same key.</summary>
    Cancel,
}
