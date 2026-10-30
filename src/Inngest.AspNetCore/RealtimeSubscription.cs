// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>What one request to a realtime relay may receive: these topics of one channel.</summary>
public sealed record RealtimeSubscription(string Channel, IReadOnlyList<string> Topics);
