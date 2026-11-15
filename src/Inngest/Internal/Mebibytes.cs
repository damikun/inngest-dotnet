// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Globalization;

namespace Inngest.Internal;

internal static class Mebibytes
{
    // Rounded up to two decimals, so a size just over a limit never reads as the limit itself.
    public static string Format(long bytes) =>
        (Math.Ceiling(bytes * 100.0 / (1024 * 1024)) / 100).ToString("0.00", CultureInfo.InvariantCulture);
}
