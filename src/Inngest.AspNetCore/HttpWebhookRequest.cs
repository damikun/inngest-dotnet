// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Microsoft.AspNetCore.Http;

namespace Inngest.AspNetCore;

internal sealed class HttpWebhookRequest(HttpContext context, ReadOnlyMemory<byte> body) : IWebhookRequest
{
    public ReadOnlyMemory<byte> Body => body;

    public IServiceProvider? Services => context.RequestServices;

    public string? GetHeader(string name) =>
        context.Request.Headers.TryGetValue(name, out var value) ? value.ToString() : null;
}
