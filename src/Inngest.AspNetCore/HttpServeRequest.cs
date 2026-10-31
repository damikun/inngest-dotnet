// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Inngest.Serving;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;

namespace Inngest.AspNetCore;

// The framework is what MapInngest and durable endpoints report; a host built on ASP.NET Core, such as Azure Functions with
// its ASP.NET Core integration, reports its own.
internal sealed class HttpServeRequest(HttpContext context, ReadOnlyMemory<byte> body, string framework = HttpServeRequest.AspNetCore) : IServeRequest
{
    public const string AspNetCore = "aspnetcore";

    private Uri? _url;

    public string Method => context.Request.Method;

    // Built on first use: only syncs need it. Behind a proxy it reflects the forwarded headers when the app uses
    // the forwarded headers middleware; InngestOptions.ServeOrigin covers the other cases.
    public Uri Url => _url ??= new Uri(context.Request.GetEncodedUrl());

    public ReadOnlyMemory<byte> Body => body;

    public string Framework => framework;

    public IServiceProvider? Services => context.RequestServices;

    public string? GetHeader(string name) =>
        context.Request.Headers.TryGetValue(name, out var value) ? value.ToString() : null;

    public string? GetQuery(string name) =>
        context.Request.Query.TryGetValue(name, out var value) ? value.ToString() : null;
}
