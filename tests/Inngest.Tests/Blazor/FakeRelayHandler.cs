using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Channels;
using Inngest.Tests.Realtime;

namespace Inngest.Tests.Blazor;

// The server behind the HttpClient of the relay source: each request gets the next answer the test provides, waiting
// until the test provides one.
internal sealed class FakeRelayHandler : HttpMessageHandler
{
    private static readonly HttpRequestOptionsKey<bool> StreamingResponse = new("WebAssemblyEnableStreamingResponse");

    private readonly Channel<Func<HttpResponseMessage>> _answers = Channel.CreateUnbounded<Func<HttpResponseMessage>>();
    private readonly List<RelayRequest> _requests = [];

    public IReadOnlyList<RelayRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    // A response that stays open and carries what the test writes to the feed.
    public void Answer(SseFeed feed) => _answers.Writer.TryWrite(() =>
    {
        var response = feed.Response();
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return response;
    });

    public void Answer(HttpStatusCode status, string? contentType = null, string body = "") => _answers.Writer.TryWrite(() =>
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8) };
        response.Content.Headers.ContentType = contentType is null ? null : new MediaTypeHeaderValue(contentType);
        return response;
    });

    // The request fails before there is a response, as it does when the network is down.
    public void Fail(Exception exception) => _answers.Writer.TryWrite(() => throw exception);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_requests)
        {
            _requests.Add(new RelayRequest(
                request.RequestUri!,
                [.. request.Headers.Accept.Select(accept => accept.MediaType!)],
                request.Options.TryGetValue(StreamingResponse, out var streaming) && streaming));
        }

        var answer = await _answers.Reader.ReadAsync(cancellationToken);
        return answer();
    }
}

internal sealed record RelayRequest(Uri Url, string[] Accept, bool StreamsTheResponse);
