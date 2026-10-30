using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Inngest.Tests.Webhooks;

// A request for the verifiers: a body and headers, with names that are not case-sensitive, as on every host.
internal sealed class TestWebhookRequest(ReadOnlyMemory<byte> body) : IWebhookRequest
{
    private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);

    public TestWebhookRequest(string body)
        : this(Encoding.UTF8.GetBytes(body))
    {
    }

    public ReadOnlyMemory<byte> Body => body;

    public IServiceProvider? Services { get; init; }

    public TestWebhookRequest With(string name, string value)
    {
        _headers[name] = value;
        return this;
    }

    public string? GetHeader(string name) => _headers.GetValueOrDefault(name);
}

// What the providers document, written out again here with the framework's HMAC, apart from the code under test.
internal static class Signing
{
    public static string Hex(ReadOnlySpan<byte> hash) => Convert.ToHexString(hash).ToLowerInvariant();

    // v1,<base64> over {id}.{timestamp}.{body}, keyed with the bytes inside whsec_<base64>.
    public static string StandardWebhooks(string secret, string id, long timestamp, ReadOnlySpan<byte> body)
    {
        var key = Convert.FromBase64String(secret["whsec_".Length..]);
        return "v1," + Convert.ToBase64String(HMACSHA256.HashData(key, Concat(Encoding.UTF8.GetBytes($"{id}.{timestamp}."), body)));
    }

    // The hex HMAC-SHA256 of {t}.{body}, keyed with the text of the secret.
    public static string Stripe(string secret, long timestamp, ReadOnlySpan<byte> body) =>
        Hex(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Concat(Encoding.UTF8.GetBytes($"{timestamp}."), body)));

    public static string StripeHeader(string secret, long timestamp, ReadOnlySpan<byte> body) =>
        $"t={timestamp},v1={Stripe(secret, timestamp, body)}";

    public static byte[] Concat(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        var result = new byte[first.Length + second.Length];
        first.CopyTo(result);
        second.CopyTo(result.AsSpan(first.Length));
        return result;
    }
}

internal sealed record SentEvent(string Name, string? Id, string DataJson);

// Takes a copy of what is sent, since the data of a webhook's event lives only as long as the request does.
internal sealed class RecordingInngestClient : IInngestClient
{
    private readonly List<SentEvent> _sent = [];

    public string AppId => "test";

    public IReadOnlyList<SentEvent> Sent
    {
        get
        {
            lock (_sent)
            {
                return [.. _sent];
            }
        }
    }

    // Thrown by the next sends instead of recording them.
    public Exception? Fail { get; set; }

    // Makes sends wait until they are cancelled.
    public bool Block { get; set; }

    public bool Cancelled { get; private set; }

    public async Task<SendEventsResult> SendAsync(InngestEvent @event, CancellationToken cancellationToken = default)
    {
        if (Fail is not null)
        {
            throw Fail;
        }

        if (Block)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }
        }

        var json = @event.Data is JsonElement element ? element.GetRawText() : JsonSerializer.Serialize(@event.Data);
        lock (_sent)
        {
            _sent.Add(new SentEvent(@event.Name, @event.Id, json));
        }

        return new SendEventsResult(["01HQ0000000000000000000001"]);
    }

    public Task<SendEventsResult> SendAsync(IEnumerable<InngestEvent> events, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
