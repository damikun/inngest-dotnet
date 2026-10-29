// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest.Internal;

// Reads INNGEST_* variables through a delegate (IConfiguration or the process environment). Blank values count
// as unset.
internal readonly struct VariableSource(Func<string, string?> read)
{
    public string? Get(string name)
    {
        var value = read(name)?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public string? GetFirst(ReadOnlySpan<string> names)
    {
        foreach (var name in names)
        {
            if (Get(name) is { } value)
            {
                return value;
            }
        }

        return null;
    }

    public Uri? GetUrl(string name) => Get(name) is { } value ? ParseUrl(name, value) : null;

    // Accepts "host:port" without a scheme, as the dev server is usually written, and assumes http for it.
    public static Uri ParseUrl(string name, string value)
    {
        var text = value.Contains("://", StringComparison.Ordinal) ? value : "http://" + value;
        if (Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
        {
            return url;
        }

        throw new InvalidOperationException($"{name} must be an http or https URL, but it is '{value}'.");
    }

    public static bool? ParseBoolean(string? value) => value?.Trim() switch
    {
        "1" => true,
        "0" => false,
        { } text when bool.TryParse(text, out var flag) => flag,
        _ => null,
    };
}
