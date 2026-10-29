using System.Net;

namespace Inngest.Benchmarks.Support;

// Stands in for the network. Nothing in the engine benchmarks should reach it. If something does, the empty
// reply is not what the SDK expects and the setup check fails.
internal sealed class NoOpHttpHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
}

// Answers every event send with a fixed body, and can keep what the client sent for a check.
internal sealed class SendEventsHandler(byte[] response) : HttpMessageHandler
{
    public bool Capture { get; set; }

    public Uri? Url { get; private set; }

    public byte[]? Body { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Capture ? CaptureAsync(request, cancellationToken) : Task.FromResult(CreateResponse());

    private async Task<HttpResponseMessage> CaptureAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Url = request.RequestUri;
        Body = await request.Content!.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return CreateResponse();
    }

    private HttpResponseMessage CreateResponse() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(response) };
}

// A clock that stands still, so that a signature made in setup never expires while a benchmark runs.
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

// Overrides nothing, so every hook runs its default and does no work.
internal sealed class NoOpMiddleware : InngestMiddleware;
