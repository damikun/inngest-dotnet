// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Diagnostics.CodeAnalysis;
using Inngest.Serving;

namespace Inngest;

/// <summary>
/// How a durable endpoint runs as an Inngest function. Each request is a run: its steps run inline and the client gets
/// the endpoint's response. When a step has to wait or fails, Inngest takes the run over, calls the endpoint again
/// once it can go on, and the client is sent to wait for the response. Durable endpoints are beta in Inngest, so this
/// API may still change.
/// </summary>
[Experimental(DurableEndpointDiagnostics.Id)]
public sealed class DurableEndpointOptions
{
    /// <summary>
    /// The function id. Null takes the method and the route pattern, such as "POST /orders/{id}", so that every order
    /// shares one function.
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// How often Inngest retries a failed step, or the endpoint when it throws. Null means 3, as in the TypeScript SDK.
    /// </summary>
    public int? Retries { get; set; }

    /// <summary>
    /// What the client gets when Inngest takes the run over. The default redirects it to a URL that answers with the
    /// endpoint's response once the run is done.
    /// </summary>
    public DurableEndpointResponse AsyncResponse { get; set; } = DurableEndpointResponse.Redirect;

    /// <summary>
    /// Where the redirect goes. Null redirects to the Inngest API, which waits up to five minutes for the run and then
    /// answers with the endpoint's status, headers and body.
    /// </summary>
    public Func<DurableEndpointRun, Uri>? RedirectUrl { get; set; }

    /// <summary>
    /// The largest request body a run stores. Inngest sends it back with every call that resumes the run, so a larger
    /// request is refused with 413. Defaults to 1 MiB; Inngest takes at most about 3 MiB.
    /// </summary>
    public int MaxRequestBodySize { get; set; } = 1024 * 1024;

    internal int RetriesOrDefault => Retries is { } retries ? Math.Max(0, retries) : EndpointCalls.DefaultRetries;
}

/// <summary>What the client of a durable endpoint gets when Inngest takes its run over.</summary>
[Experimental(DurableEndpointDiagnostics.Id)]
public enum DurableEndpointResponse
{
    /// <summary>303 See Other to a URL that answers with the endpoint's response once the run is done.</summary>
    Redirect,

    /// <summary>200 with {"run_id", "token"}, for clients that wait for the run themselves.</summary>
    Token,
}

/// <summary>A durable endpoint run that Inngest took over, and the token that reads its output.</summary>
[Experimental(DurableEndpointDiagnostics.Id)]
public sealed record DurableEndpointRun(string RunId, string Token);

/// <summary>
/// Marks a controller or action as a durable endpoint, as WithDurableEndpoint() does for minimal API endpoints. The
/// app must call app.UseInngestDurableEndpoints().
/// </summary>
[Experimental(DurableEndpointDiagnostics.Id)]
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
public sealed class DurableEndpointAttribute(string? id = null) : Attribute, IDurableEndpointMetadata
{
    /// <summary>The function id; null takes the method and the route pattern.</summary>
    public string? Id { get; } = id;

    /// <summary>How often Inngest retries a failed step or the endpoint. Negative means 3.</summary>
    public int Retries { get; set; } = -1;

    /// <summary>What the client gets when Inngest takes the run over.</summary>
    public DurableEndpointResponse AsyncResponse { get; set; } = DurableEndpointResponse.Redirect;

    DurableEndpointOptions IDurableEndpointMetadata.Options => new()
    {
        Id = Id,
        Retries = Retries < 0 ? null : Retries,
        AsyncResponse = AsyncResponse,
    };
}

// The endpoint metadata the middleware looks for, from WithDurableEndpoint() or the attribute.
internal interface IDurableEndpointMetadata
{
    DurableEndpointOptions Options { get; }
}

internal sealed class DurableEndpointMetadata(DurableEndpointOptions options) : IDurableEndpointMetadata
{
    public DurableEndpointOptions Options { get; } = options;
}

/// <summary>The diagnostic that marks the durable endpoint API, which follows a feature Inngest still calls beta.</summary>
public static class DurableEndpointDiagnostics
{
    /// <summary>Suppress it with &lt;NoWarn&gt;$(NoWarn);INNGEST001&lt;/NoWarn&gt; or #pragma warning disable INNGEST001.</summary>
    public const string Id = "INNGEST001";
}
