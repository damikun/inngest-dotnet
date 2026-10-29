// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest.Connect;

// The waits of the TypeScript SDK between attempts to reach Inngest, which never gives up.
internal static class Backoff
{
    private static readonly TimeSpan[] Schedule =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
    ];

    // The wait after the given number of failures in a row; from the ninth on it stays at five minutes.
    public static TimeSpan AfterFailures(int failures) => Schedule[Math.Clamp(failures - 1, 0, Schedule.Length - 1)];

    // A draining gateway is replaced quickly, so the next attempt only waits long enough to spread workers out.
    public static TimeSpan GatewayDraining() => TimeSpan.FromMilliseconds(500 + Random.Shared.Next(1000));
}
