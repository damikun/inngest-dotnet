// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inngest.Internal;

// The public types the SDK itself reads and writes with the user's serializer options. SerializerOptionsSetup
// appends this to the options when the app's own resolvers do not cover them.
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(FunctionFailure))]
[JsonSerializable(typeof(FunctionError))]
[JsonSerializable(typeof(FunctionFinished))]
[JsonSerializable(typeof(FunctionCancelled))]
[JsonSerializable(typeof(SendEventsResult))]
internal sealed partial class PublicTypesJsonContext : JsonSerializerContext
{
}
