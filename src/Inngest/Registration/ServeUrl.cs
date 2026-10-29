// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest.Registration;

// The URLs a sync reports for the app. The deployId query parameter identifies the sync request, not the app,
// so it is dropped; every other query parameter is kept exactly as received, because it may be part of how the
// app is routed.
internal sealed class ServeUrl
{
    private const string DeployIdParameter = "deployId";
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
