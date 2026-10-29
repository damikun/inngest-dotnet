using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Inngest.IntegrationTests.Harness;

// Every request that reached the app, recorded by middleware in front of the SDK's endpoint. It shows what the Inngest
// server sent even when the SDK failed to handle it, which is how a test tells a broken harness from a broken SDK.
public sealed class RequestLog
{
    private const string SignatureHeader = "X-Inngest-Signature";

    private readonly ConcurrentQueue<ServedRequest> _requests = new();

    public IReadOnlyList<ServedRequest> Snapshot() => [.. _requests];

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var request = context.Request;
        var receivedAt = DateTimeOffset.UtcNow;

        // Sync bodies are tiny and are what the server signs, so keep them to check signatures against.
        byte[]? body = null;
        if (HttpMethods.IsPut(request.Method))
        {
            request.EnableBuffering();
            using var copy = new MemoryStream();
            await request.Body.CopyToAsync(copy, context.RequestAborted);
            request.Body.Position = 0;
            body = copy.ToArray();
        }

        try
        {
            await next(context);
            Add(context, receivedAt, body, exception: null);
        }
        catch (Exception exception)
        {
            Add(context, receivedAt, body, exception);
            throw;
        }
    }

    private void Add(HttpContext context, DateTimeOffset receivedAt, byte[]? body, Exception? exception)
    {
        var request = context.Request;
        _requests.Enqueue(new ServedRequest(
            receivedAt,
            request.Method,
            $"{request.Path}{request.QueryString}",
            request.Headers["X-Inngest-Server-Kind"].ToString() is { Length: > 0 } kind ? kind : null,
            request.Headers[SignatureHeader].ToString() is { Length: > 0 } signature ? signature : null,
            body,
            exception is null ? context.Response.StatusCode : StatusCodes.Status500InternalServerError,
            context.Response.Headers.ContainsKey(SignatureHeader),
            exception is null ? null : $"{exception.GetType().Name}: {exception.Message}"));
    }
}

public sealed record ServedRequest(
    DateTimeOffset ReceivedAt,
    string Method,
    string Target,
    string? ServerKind,
    string? Signature,
    byte[]? Body,
    int StatusCode,
    bool ResponseSigned,
    string? Exception)
{
    public bool Signed => Signature is not null;

    public string BodyText => Body is null ? string.Empty : Encoding.UTF8.GetString(Body);

    public override string ToString() =>
        $"{ReceivedAt:HH:mm:ss.fff} {Method} {Target} -> {StatusCode}" +
        $" (server kind {ServerKind ?? "none"}, {(Signed ? "signed" : "unsigned")}, response {(ResponseSigned ? "signed" : "unsigned")})" +
        (Exception is null ? string.Empty : $" threw {Exception}");
}
