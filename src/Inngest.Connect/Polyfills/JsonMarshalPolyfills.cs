// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

#if !NET9_0_OR_GREATER
using System.Text;
using System.Text.Json;

namespace System.Runtime.InteropServices;

// JsonMarshal.GetRawUtf8Value from .NET 9. Older runtimes cannot expose the document's own buffer, so this copies.
internal static class JsonMarshal
{
    public static ReadOnlySpan<byte> GetRawUtf8Value(JsonElement element) => Encoding.UTF8.GetBytes(element.GetRawText());
}
#endif
