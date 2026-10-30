// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// One kind of realtime message within a channel, such as the topic "status" of the channel "document:abc123". A
/// channel is a stream for one thing users watch; subscribers choose the topics they receive.
/// </summary>
public sealed record RealtimeTopic(string Channel, string Name);
