// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text;

namespace Inngest.Registration;

// The URLs the app is served at: the one Inngest should call, and the app and step URLs a sync reports. The deployId
// query parameter identifies one sync request, not the app, so it is dropped; every other query parameter is kept
// exactly as received, because it may be part of how the app is routed.
internal sealed class ServeUrl
{
    public const string DeployIdParameter = "deployId";

    private const string FunctionIdParameter = "fnId";
    private const string StepIdParameter = "stepId";

    private readonly string _path;
    private readonly string _appQuery;
    private readonly string _stepQuery;

    public ServeUrl(Uri serveUrl)
    {
        ArgumentNullException.ThrowIfNull(serveUrl);
        if (!serveUrl.IsAbsoluteUri)
        {
            throw new ArgumentException("The serve URL must be absolute.", nameof(serveUrl));
        }

        // The query is rebuilt below, and a fragment never reaches a server.
        _path = serveUrl.GetLeftPart(UriPartial.Path);
        _appQuery = QueryWithout(serveUrl.Query, DeployIdParameter);
        _stepQuery = QueryWithout(serveUrl.Query, DeployIdParameter, FunctionIdParameter, StepIdParameter);
    }

    // The app's own URL.
    public string App => _appQuery.Length == 0 ? _path : $"{_path}?{_appQuery}";

    // The URL Inngest calls to run a function. The function is addressed by its composite id in fnId, and
    // stepId is "step" for the call that starts a run.
    public string Step(string compositeId)
    {
        var addressing = $"{FunctionIdParameter}={Uri.EscapeDataString(compositeId)}&{StepIdParameter}=step";
        return _stepQuery.Length == 0 ? $"{_path}?{addressing}" : $"{_path}?{_stepQuery}&{addressing}";
    }

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

    // Uri has already normalized the query, so names can be compared as they are.
    private static string QueryWithout(string query, params ReadOnlySpan<string> names)
    {
        var kept = new List<string>();
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var name = separator < 0 ? pair : pair[..separator];
            if (!names.Contains(name))
            {
                kept.Add(pair);
            }
        }

        return string.Join('&', kept);
    }
}
