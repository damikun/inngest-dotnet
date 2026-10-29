// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Globalization;
using Inngest.Internal;

namespace Inngest.Testing;

// Reads the times the SDK sends Inngest in ops: durations such as "1h30m" and RFC 3339 instants.
internal static class TimeStrings
{
    public static bool TryParseDuration(string? text, out TimeSpan duration) => TimeString.TryParse(text, out duration);

    public static bool TryParseInstant(string? text, out DateTimeOffset instant) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out instant);
}
