// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>The ids Inngest assigned to the sent events, in the order they were sent.</summary>
public sealed record SendEventsResult(IReadOnlyList<string> Ids);
