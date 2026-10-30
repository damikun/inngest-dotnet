// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text;

namespace Inngest.AwsLambda;

// Query strings of API Gateway events. Events carry them as the raw string (HTTP APIs) or as dictionaries of decoded
// values (REST APIs). Both end up as a percent-encoded string here, which one parser reads, so that a parameter has
// the same value whichever source the event came from.
internal static class LambdaQuery
{
    // The query string for an event that has only dictionaries. REST APIs list every value of a repeated parameter in
    // the multi-value form and only the last in the single-value form.
    public static string Build(IDictionary<string, IList<string>>? multiValues, IDictionary<string, string>? values)
    {
        var query = new StringBuilder();
        if (multiValues is { Count: > 0 })
        {
            foreach (var (name, list) in multiValues)
            {
                if (list is null)
                {
                    continue;
                }

                foreach (var value in list)
                {
                    Append(query, name, value);
                }
            }
        }
        else if (values is { Count: > 0 })
        {
            foreach (var (name, value) in values)
            {
                Append(query, name, value);
            }
        }

        return query.ToString();
    }

    // The decoded value of a parameter, or null when it is absent. Names match without regard to case, as the query
    // collection of ASP.NET Core does, and the values of a repeated parameter are joined with commas, which is how it
    // reads as text.
    public static string? Find(string query, string name)
    {
        string? found = null;
        var remaining = query.AsSpan();
        while (!remaining.IsEmpty)
        {
            var end = remaining.IndexOf('&');
            var pair = end < 0 ? remaining : remaining[..end];
            remaining = end < 0 ? default : remaining[(end + 1)..];

            var separator = pair.IndexOf('=');
            var rawName = separator < 0 ? pair : pair[..separator];
            if (rawName.IsEmpty || !Matches(rawName, name))
            {
                continue;
            }

            var value = separator < 0 ? string.Empty : Decode(pair[(separator + 1)..]);
            found = found is null ? value : $"{found},{value}";
        }

        return found;
    }

    private static void Append(StringBuilder query, string name, string? value)
    {
        if (query.Length > 0)
        {
            query.Append('&');
        }

        query.Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value ?? string.Empty));
    }

    private static bool Matches(ReadOnlySpan<char> rawName, string name) =>
        rawName.IndexOfAny('%', '+') < 0 ? rawName.Equals(name, StringComparison.OrdinalIgnoreCase) : Decode(rawName).Equals(name, StringComparison.OrdinalIgnoreCase);

    // A plus sign is a space in a query string, and %2B is a plus sign, so spaces come first.
    private static string Decode(ReadOnlySpan<char> raw) =>
        raw.IndexOfAny('%', '+') < 0 ? new string(raw) : Uri.UnescapeDataString(raw.ToString().Replace('+', ' '));
}
