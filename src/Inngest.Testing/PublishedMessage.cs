// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;

namespace Inngest.Testing;

/// <summary>A realtime message the test host recorded instead of publishing it, with its data as JSON.</summary>
public sealed record PublishedMessage(RealtimeTopic Topic, JsonElement Data);
