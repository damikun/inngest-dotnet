// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text;

namespace Inngest.Serving;

internal static class ServeUrl
{
    public const string DeployIdParameter = "deployId";

    // The URL Inngest should call the app on: the URL a sync named, with ServePath and then ServeOrigin applied as
    // in the TypeScript SDK. deployId is dropped because it belongs to a single deploy, not to the app.
    public static Uri Resolve(Uri url, Uri? serveOrigin, string? servePath)
    {
        ArgumentNullException.ThrowIfNull(url);

        var origin = (serveOrigin ?? url).GetLeftPart(UriPartial.Authority);
        var path = string.IsNullOrEmpty(servePath) ? url.AbsolutePath
            : servePath.StartsWith('/') ? servePath
            : "/" + servePath;

        return new Uri(string.Concat(origin, path, WithoutDeployId(url.Query)));
    }

    private static string WithoutDeployId(string query)
    {
        if (query.Length <= 1)
        {
            return "";
        }

        if (!query.Contains(DeployIdParameter, StringComparison.OrdinalIgnoreCase))
        {
            return query;
        }

        // Matched without regard to case, like the query lookups of ASP.NET Core.
        var parameters = query.AsSpan(1);
        var result = new StringBuilder(query.Length);
        foreach (var range in parameters.Split('&'))
        {
            var parameter = parameters[range];
            var separator = parameter.IndexOf('=');
            var name = separator < 0 ? parameter : parameter[..separator];
            if (!parameter.IsEmpty && !name.Equals(DeployIdParameter, StringComparison.OrdinalIgnoreCase))
            {
                result.Append(result.Length == 0 ? '?' : '&').Append(parameter);
            }
        }

        return result.ToString();
    }
}
