// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Buffers;
using Inngest.Webhooks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Inngest.AspNetCore;

// The ASP.NET Core host of a webhook endpoint: it reads the body, within the limit and without parsing it, hands the
// request to the receiver and writes the status code of the outcome. The answer has no body.
internal static class WebhookEndpoint
{
    // The declared length sizes the first buffer only up to this much, since it comes from a caller who has not proved
    // who it is. A larger body grows the buffer as it arrives.
    private const int MaxInitialBufferSize = 64 * 1024;

    public static async Task HandleAsync(HttpContext context, WebhookReceiver receiver)
    {
        var request = context.Request;
        WebhookOutcome outcome;

        // A body that says it is too large is refused without reading any of it.
        if (request.ContentLength > receiver.MaxBodySize)
        {
            outcome = receiver.BodyTooLarge();
        }
        else
        {
            using var body = await ReadBodyAsync(context, receiver.MaxBodySize, context.RequestAborted).ConfigureAwait(false);
            outcome = body is null
                ? receiver.BodyTooLarge()
                : await receiver.ReceiveAsync(new HttpWebhookRequest(context, body.Memory), context.RequestAborted).ConfigureAwait(false);
        }

        context.Response.StatusCode = outcome.StatusCode();
    }

    // The body in a pooled buffer that is cleared when it is returned, since webhooks carry personal and payment data, or
    // null when it is longer than the limit. Consumes the pipe as it reads; holding unconsumed data would stall Kestrel
    // once the body passes its request buffer limit.
    private static async Task<RentedBody?> ReadBodyAsync(HttpContext context, int limit, CancellationToken cancellationToken)
    {
        var request = context.Request;
        if (request.ContentLength == 0 || context.Features.Get<IHttpRequestBodyDetectionFeature>() is { CanHaveBody: false })
        {
            return RentedBody.Empty;
        }

        var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Clamp(request.ContentLength ?? 0, 1, Math.Min(limit, MaxInitialBufferSize)));
        var length = 0;
        try
        {
            var reader = request.BodyReader;
            while (true)
            {
                var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var sequence = result.Buffer;
                if (sequence.Length > limit - length)
                {
                    reader.AdvanceTo(sequence.End);
                    RentedBody.Return(buffer, length);
                    return null;
                }

                foreach (var segment in sequence)
                {
                    if (buffer.Length - length < segment.Length)
                    {
                        var larger = ArrayPool<byte>.Shared.Rent(Math.Max(length + segment.Length, buffer.Length * 2));
                        buffer.AsSpan(0, length).CopyTo(larger);
                        RentedBody.Return(buffer, length);
                        buffer = larger;
                    }

                    segment.Span.CopyTo(buffer.AsSpan(length));
                    length += segment.Length;
                }

                reader.AdvanceTo(sequence.End);
                if (result.IsCompleted)
                {
                    return new RentedBody(buffer, length);
                }
            }
        }
        catch
        {
            RentedBody.Return(buffer, length);
            throw;
        }
    }

    private sealed class RentedBody(byte[] array, int length) : IDisposable
    {
        public static readonly RentedBody Empty = new([], 0);

        private byte[]? _array = array;

        public ReadOnlyMemory<byte> Memory => _array.AsMemory(0, length);

        public static void Return(byte[] array, int length)
        {
            array.AsSpan(0, length).Clear();
            ArrayPool<byte>.Shared.Return(array);
        }

        public void Dispose()
        {
            var rented = _array;
            _array = null;
            if (rented is { Length: > 0 })
            {
                Return(rented, length);
            }
        }
    }
}
