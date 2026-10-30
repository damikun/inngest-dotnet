// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using Inngest.Internal;
using Inngest.Serving;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.AspNetCore;

internal static class InngestEndpoint
{
    // The declared length sizes the first buffer only up to this much. It comes from a caller whose signature cannot
    // be checked before the body has arrived, so trusting it further would let anyone make the app reserve megabytes
    // per request by declaring a large body and sending it slowly. Larger bodies grow the buffer as they arrive.
    private const int MaxInitialBufferSize = 64 * 1024;

    // How often a streamed call writes to the connection, and how long a call may take before it is streamed: the
    // interval of the TypeScript SDK.
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(3);

    private static readonly ReadOnlyMemory<byte> KeepAlive = " "u8.ToArray();

    public static async Task HandleAsync(HttpContext context, InngestCommHandler handler)
    {
        var cancellationToken = context.RequestAborted;
        using var body = await ReadBodyAsync(context, cancellationToken).ConfigureAwait(false);
        var request = new HttpServeRequest(context, body?.WrittenMemory ?? ReadOnlyMemory<byte>.Empty);
        var pending = handler.HandleAsync(request, cancellationToken);
        if (handler.Streams(request))
        {
            var time = context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
            if (!await CompletesWithinIntervalAsync(pending, time, cancellationToken).ConfigureAwait(false))
            {
                await StreamAsync(context, pending, time, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        using var response = await pending.ConfigureAwait(false);
        await WriteAsync(context, response, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task WriteAsync(HttpContext context, ServeResponse response, CancellationToken cancellationToken)
    {
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

    // A 201 tells Inngest that the reply is streamed: whitespace while the call runs, which proxies and load
    // balancers see as traffic, then the response as {"status", "headers", "body"} with the body as a JSON string,
    // which is how Inngest reads a 201. The headers, the signature among them, travel inside, as in inngest-js.
    private static async Task StreamAsync(HttpContext context, Task<ServeResponse> pending, TimeProvider time, CancellationToken cancellationToken)
    {
        var httpResponse = context.Response;
        httpResponse.StatusCode = StatusCodes.Status201Created;
        httpResponse.ContentType = "application/json";

        // nginx would otherwise hold the whitespace back until the reply ends.
        httpResponse.Headers["X-Accel-Buffering"] = "no";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        try
        {
            do
            {
                await httpResponse.BodyWriter.WriteAsync(KeepAlive, cancellationToken).ConfigureAwait(false);
            }
            while (!await CompletesWithinIntervalAsync(pending, time, cancellationToken).ConfigureAwait(false));
        }
        catch
        {
            // The call goes on without its caller. Its response still owns pooled memory, and a failure is observed here
            // so that it is not reported as unobserved.
            _ = pending.ContinueWith(
                static task =>
                {
                    if (task.IsCompletedSuccessfully)
                    {
                        task.Result.Dispose();
                    }
                    else
                    {
                        _ = task.Exception;
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }

        using var response = await pending.ConfigureAwait(false);
        using (var writer = new Utf8JsonWriter(httpResponse.BodyWriter))
        {
            writer.WriteStartObject();
            writer.WriteNumber("status"u8, response.StatusCode);
            writer.WriteStartObject("headers"u8);
            foreach (var (name, value) in response.Headers)
            {
                writer.WriteString(name, value);
            }

            writer.WriteEndObject();
            writer.WriteString("body"u8, response.Body.Span);
            writer.WriteEndObject();
        }

        await httpResponse.BodyWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> CompletesWithinIntervalAsync(Task task, TimeProvider time, CancellationToken cancellationToken)
    {
        if (task.IsCompleted)
        {
            return true;
        }

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var completed = await Task.WhenAny(task, Task.Delay(KeepAliveInterval, time, timer.Token)).ConfigureAwait(false);
        await timer.CancelAsync().ConfigureAwait(false);
        return completed == task;
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
