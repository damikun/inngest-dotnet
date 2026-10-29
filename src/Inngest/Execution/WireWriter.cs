// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Buffers;
using System.Text.Json;

namespace Inngest.Execution;

internal static class WireWriter
{
    // Responses embed user data, so they use the app's encoder and escape it the way the app configured.
    public static Utf8JsonWriter Create(IBufferWriter<byte> output, JsonSerializerOptions options) =>
        new(output, new JsonWriterOptions { Encoder = options.Encoder });
}
