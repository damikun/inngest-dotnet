// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest.Encryption;

internal static class EncryptionKeys
{
    // The characters String.prototype.trim removes. Char.IsWhiteSpace differs in two: it keeps U+FEFF and trims U+0085,
    // and a key that one trims differently is a different key.
    private static readonly char[] JavaScriptWhiteSpace =
    [
        '\t', '\n', '\v', '\f', '\r', ' ', '\u00A0', '\u1680',
        '\u2000', '\u2001', '\u2002', '\u2003', '\u2004', '\u2005', '\u2006', '\u2007', '\u2008', '\u2009', '\u200A',
        '\u2028', '\u2029', '\u202F', '\u205F', '\u3000', '\uFEFF',
    ];

    // The keys in the order they are tried: the key, then the fallbacks. As in the TypeScript package, surrounding white
    // space is not part of a key and empty keys are dropped.
    public static string[] Normalize(string? key, IReadOnlyList<string>? fallbacks)
    {
        List<string> keys = [];
        Add(keys, key);
        if (fallbacks is not null)
        {
            foreach (var fallback in fallbacks)
            {
                Add(keys, fallback);
            }
        }

        return [.. keys];
    }

    private static void Add(List<string> keys, string? key)
    {
        var trimmed = key?.Trim(JavaScriptWhiteSpace);
        if (!string.IsNullOrEmpty(trimmed))
        {
            keys.Add(trimmed);
        }
    }
}
