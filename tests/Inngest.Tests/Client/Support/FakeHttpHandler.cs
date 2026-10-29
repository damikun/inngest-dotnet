using System.Net;
using System.Text;

namespace Inngest.Tests.Client;

// Answers requests from a script instead of the network, and records what was sent.
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly Queue<Step> _script = new();
    private readonly List<RecordedRequest> _requests = [];
    private Step? _fallback;

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public RecordedRequest Single => Requests.ShouldHaveSingleItem();

    // Answers the next unanswered request.
    public FakeHttpHandler Respond(HttpStatusCode status, string body = "", string contentType = "application/json")
    {
        lock (_gate)
        {
            _script.Enqueue((_, _) => Task.FromResult(CreateResponse(status, body, contentType)));
        }

        return this;
    }

    // Makes the next unanswered request fail the way a refused connection or a timeout does.
    public FakeHttpHandler Fail(Exception exception)
    {
        lock (_gate)
        {
            _script.Enqueue((_, _) => Task.FromException<HttpResponseMessage>(exception));
        }

        return this;
    }

    // Answers the next unanswered request with headers and the start of a body, then breaks the connection.
    public FakeHttpHandler RespondWithTruncatedBody(HttpStatusCode status, string partialBody)
    {
        lock (_gate)
        {
            _script.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new TruncatedContent(Encoding.UTF8.GetBytes(partialBody)) }));
        }

        return this;
    }

    // Leaves the next unanswered request open until its cancellation token fires.
    public FakeHttpHandler Hang()
    {
        lock (_gate)
        {
            _script.Enqueue(async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("Unreachable.");
            });
        }

        return this;
    }

    // Answers every request that the script does not cover.
    public FakeHttpHandler RespondAlways(HttpStatusCode status, string body = "", string contentType = "application/json")
    {
        _fallback = (_, _) => Task.FromResult(CreateResponse(status, body, contentType));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var recorded = await RecordedRequest.CaptureAsync(request).ConfigureAwait(false);

        Step answer;
        lock (_gate)
        {
            _requests.Add(recorded);
            answer = _script.Count > 0
                ? _script.Dequeue()
                : _fallback ?? throw new InvalidOperationException($"No response was scripted for request {_requests.Count}: {request.Method} {request.RequestUri}");
        }

        return await answer(request, cancellationToken).ConfigureAwait(false);
    }

    private delegate Task<HttpResponseMessage> Step(HttpRequestMessage request, CancellationToken cancellationToken);

    private static HttpResponseMessage CreateResponse(HttpStatusCode status, string body, string contentType) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, contentType) };
}

internal sealed class RecordedRequest
{
    private readonly Dictionary<string, string[]> _headers;

    private RecordedRequest(HttpMethod method, Uri uri, Dictionary<string, string[]> headers, byte[] body)
    {
        Method = method;
        Uri = uri;
        _headers = headers;
        Body = body;
    }

    public HttpMethod Method { get; }

    public Uri Uri { get; }

    public byte[] Body { get; }

    public string BodyText => Encoding.UTF8.GetString(Body);

    public IReadOnlyCollection<string> HeaderNames => _headers.Keys;

    // The value of a request or content header, or null when it was not sent.
    public string? Header(string name) => _headers.TryGetValue(name, out var values) ? string.Join(", ", values) : null;

    public static async Task<RecordedRequest> CaptureAsync(HttpRequestMessage request)
    {
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            headers[header.Key] = [.. header.Value];
        }

        var body = Array.Empty<byte>();
        if (request.Content is { } content)
        {
            foreach (var header in content.Headers)
            {
                headers[header.Key] = [.. header.Value];
            }

            body = await content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }

        return new RecordedRequest(request.Method, request.RequestUri!, headers, body);
    }
}

internal sealed class TruncatedContent(byte[] partial) : HttpContent
{
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        await stream.WriteAsync(partial);
        throw new IOException("The response ended prematurely.");
    }

    protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
        Task.FromResult<Stream>(new BrokenStream(partial));

    protected override bool TryComputeLength(out long length)
    {
        length = -1;
        return false;
    }

    private sealed class BrokenStream(byte[] partial) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= partial.Length)
            {
                throw new IOException("The response ended prematurely.");
            }

            var count = Math.Min(buffer.Length, partial.Length - _position);
            partial.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return ValueTask.FromResult(count);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
