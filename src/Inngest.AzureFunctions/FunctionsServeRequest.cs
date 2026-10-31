// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Inngest.Serving;
using Microsoft.Azure.Functions.Worker.Http;

namespace Inngest.AzureFunctions;

// A request of the built-in HTTP model, whose body the worker has already received whole.
internal sealed class FunctionsServeRequest(HttpRequestData request, ReadOnlyMemory<byte> body) : IServeRequest
{
    public string Method => request.Method;

    // The host builds it from the request it received: its scheme, host, path with the route prefix (/api by default) and
    // query. The host corrects the scheme and the host name from the front end's headers only on Linux managed hosting, so
    // InngestOptions.ServeOrigin and ServePath are what a deployed app should rely on for the URL of an out-of-band sync.
    public Uri Url => request.Url;

    public ReadOnlyMemory<byte> Body => body;

    public string Framework => FunctionsEndpoint.Framework;

    // The scope of this invocation, as the services of a function are.
    public IServiceProvider? Services => request.FunctionContext.InstanceServices;

    public string? GetHeader(string name) =>
        request.Headers.TryGetValues(name, out var values) ? string.Join(',', values) : null;

    // Decoded, case-insensitive and joined with commas when repeated, like HttpRequest.Query.
    public string? GetQuery(string name) => request.Query[name];
}
