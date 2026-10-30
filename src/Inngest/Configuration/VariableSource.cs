// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest.Configuration;

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

    // Names that resolve only inside a network, where addresses are usually plain http.
    private static readonly string[] PrivateSuffixes = [".internal", ".local", ".localhost", ".lan", ".home.arpa", ".svc", ".test"];

    public Uri? GetUrl(string name) => Get(name) is { } value ? ParseUrl(name, value) : null;

    // Accepts "host:port" without a scheme, as the dev server is usually written. That means http for the dev server
    // and for hosts inside a network: localhost, an IP address, a name without dots such as a container's, or a
    // private suffix. Any other host gets https, so that a key or token never crosses the internet in clear text
    // because a scheme was left out.
    public static Uri ParseUrl(string name, string value, bool assumeHttp = false)
    {
        var text = value.Contains("://", StringComparison.Ordinal) ? value
            : assumeHttp || IsPrivateHost(value) ? "http://" + value
            : "https://" + value;
        if (Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
        {
            return url;
        }

        throw new InvalidOperationException($"{name} must be an http or https URL, but it is '{value}'.");
    }

    private static bool IsPrivateHost(string address)
    {
        if (!Uri.TryCreate("http://" + address, UriKind.Absolute, out var url))
        {
            return true;
        }

        var host = url.IdnHost;
        return url.IsLoopback
            || url.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6
            || !host.Contains('.', StringComparison.Ordinal)
            || PrivateSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    public static bool? ParseBoolean(string? value) => value?.Trim() switch
    {
        "1" => true,
        "0" => false,
        { } text when bool.TryParse(text, out var flag) => flag,
        _ => null,
    };

    // A flag in an environment variable, where on/off and yes/no are common too. INNGEST_DEV takes anything else as
    // the dev server's address, and dev mode checks no signatures, so "off" must not be taken for one.
    public static bool? ParseFlag(string? value) => ParseBoolean(value) ?? value?.Trim().ToLowerInvariant() switch
    {
        "on" or "yes" => true,
        "off" or "no" => false,
        _ => null,
    };
}
