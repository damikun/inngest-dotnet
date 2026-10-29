// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Inngest.Execution;
using Inngest.Internal;

namespace Inngest.Steps;

// Turns a request made by an Inngest function into a fetch step, which Inngest makes and memoizes. Requests made
// anywhere else, including inside step handlers, where steps cannot be nested, go out as usual.
internal sealed class StepFetchHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Outside an invocation, in a step handler and in an observer hook there is no step to make. The SDK's own
        // calls to Inngest carry its header and stay calls, even when the handler was added to every client.
        if (Invocation.AmbientSteps is not { } steps
            || request.RequestUri is not { IsAbsoluteUri: true } url
            || request.Headers.Contains(Headers.Sdk))
        {
            return base.SendAsync(request, cancellationToken);
        }

        return steps.FetchAsync(StepId(request.Method, url), request);
    }

    // The id has to be the same on every replay. inngest-js uses the host alone; the method and path are added so
    // that requests made in parallel from other threads, which reach the engine in no fixed order, still get ids of
    // their own. Repeats of the same request get ":1", ":2", ... in call order. The query is left out, as it may
    // carry keys.
    private static string StepId(HttpMethod method, Uri url) => $"fetch {method.Method} {url.Authority}{url.AbsolutePath}";
}
