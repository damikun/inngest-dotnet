// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

#if !NET9_0_OR_GREATER
namespace System.Text.Json;

// JsonElement.GetPropertyCount from .NET 9, which counts by enumerating on older runtimes.
internal static class JsonElementPolyfills
{
    extension(JsonElement element)
    {
        public int GetPropertyCount()
        {
            var count = 0;
            foreach (var _ in element.EnumerateObject())
            {
                count++;
            }

            return count;
        }
    }
}
#endif
