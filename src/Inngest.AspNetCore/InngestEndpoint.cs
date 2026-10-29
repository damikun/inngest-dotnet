// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Inngest.Internal;
using Inngest.Serving;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Inngest.AspNetCore;

internal static class InngestEndpoint
{
    // The declared length sizes the first buffer only up to this much. It comes from a caller whose signature cannot
    // be checked before the body has arrived, so trusting it further would let anyone make the app reserve megabytes
    // per request by declaring a large body and sending it slowly. Larger bodies grow the buffer as they arrive.
    private const int MaxInitialBufferSize = 64 * 1024;

    public static async Task HandleAsync(HttpContext context, InngestCommHandler handler)
    {
        var cancellationToken = context.RequestAborted;
        using var body = await ReadBodyAsync(context, cancellationToken).ConfigureAwait(false);
        using var response = await handler
            .HandleAsync(new HttpServeRequest(context, body?.WrittenMemory ?? ReadOnlyMemory<byte>.Empty), cancellationToken)
            .ConfigureAwait(false);

        var httpResponse = context.Response;
        httpResponse.StatusCode = response.StatusCode;
        var headers = response.Headers;
        for (var i = 0; i < headers.Count; i++)
        {
            httpResponse.Headers[headers[i].Key] = headers[i].Value;
        }

        httpResponse.ContentLength = response.Body.Length;
        if (!response.Body.IsEmpty)
        {
            await httpResponse.BodyWriter.WriteAsync(response.Body, cancellationToken).ConfigureAwait(false);
        }
    }

    // Consumes the pipe as it reads; holding unconsumed data would stall Kestrel once the body passes its
    // request buffer limit.
    internal static async Task<PooledBufferWriter?> ReadBodyAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var request = context.Request;
        if (request.ContentLength == 0 || context.Features.Get<IHttpRequestBodyDetectionFeature>() is { CanHaveBody: false })
        {
            return null;
        }

        var buffer = new PooledBufferWriter((int)Math.Min(request.ContentLength ?? 0, MaxInitialBufferSize));
        try
        {
            var reader = request.BodyReader;
            while (true)
            {
                var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                foreach (var segment in result.Buffer)
                {
                    buffer.Write(segment.Span);
                }

                reader.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted)
                {
                    return buffer;
                }
            }
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }
}
