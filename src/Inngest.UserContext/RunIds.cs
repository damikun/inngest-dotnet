// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

namespace Inngest.UserContext;

// Inngest gives runs ULIDs: 26 characters of Crockford's base 32 whose first ten are the time the run was created, in
// Unix milliseconds. Inngest makes the id, so a sender of events cannot choose it, and every call of a run has the same
// one, which makes it the clock to judge a payload's expiry by.
internal static class RunIds
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int Length = 26;
    private const int TimeLength = 10;
    private const long MaxMilliseconds = 253402300799999;

    // Null for an id that is not a ULID, such as the made-up ids of a test.
    public static DateTimeOffset? StartOf(string? runId)
    {
        if (runId is null || runId.Length != Length)
        {
            return null;
        }

        long milliseconds = 0;
        for (var i = 0; i < Length; i++)
        {
            var value = Alphabet.IndexOf(char.ToUpperInvariant(runId[i]), StringComparison.Ordinal);
            if (value < 0)
            {
                return null;
            }

            if (i < TimeLength)
            {
                milliseconds = (milliseconds << 5) | (uint)value;
            }
        }

        // The first character carries only three bits, and DateTimeOffset ends in the year 9999.
        return runId[0] <= '7' && milliseconds <= MaxMilliseconds ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) : null;
    }
}
