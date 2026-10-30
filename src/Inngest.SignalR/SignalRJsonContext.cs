// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json.Serialization;

namespace Inngest.SignalR;

// The types that the hub and the bridge put on the wire: the topics a client sends and the text of a message. SignalR's
// JSON protocol finds them by reflection, which a trimmed or Native AOT app does not have, so AddInngestSignalR
// registers this context with the protocol in such an app.
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class SignalRJsonContext : JsonSerializerContext;
