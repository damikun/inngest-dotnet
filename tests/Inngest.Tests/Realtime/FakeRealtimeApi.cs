using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Threading.Channels;
using Inngest.Realtime;

namespace Inngest.Tests.Realtime;

// Inngest's realtime API with subscriptions the test scripts: each one opened takes the next scripted answer, waiting
// until the test provides it.
internal sealed class FakeRealtimeApi : IRealtimeApi
{
    private readonly Channel<Func<HttpResponseMessage>> _answers = Channel.CreateUnbounded<Func<HttpResponseMessage>>();
    private int _tokens;

    public int TokensMinted => Volatile.Read(ref _tokens);

    public Exception? TokenFailure { get; set; }

    public List<CancellationToken> Subscriptions { get; } = [];

    public List<(RealtimeTopic Topic, string? RunId, string Body)> Published { get; } = [];

    public void Answer(SseFeed feed) => _answers.Writer.TryWrite(feed.Response);

    public void Fail(Exception exception) => _answers.Writer.TryWrite(() => throw exception);

    public Task PublishAsync(RealtimeTopic topic, string? runId, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        lock (Published)
        {
            Published.Add((topic, runId, Encoding.UTF8.GetString(body.Span)));
        }

        return Task.CompletedTask;
    }

    public async Task<string> PublishStreamAsync(RealtimeTopic topic, string? runId, IAsyncEnumerable<string> chunks, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        await foreach (var chunk in chunks.WithCancellation(cancellationToken))
        {
            text.Append(chunk);
        }

        lock (Published)
        {
            Published.Add((topic, runId, text.ToString()));
        }

        return text.ToString();
    }

    public Task<string> GetTokenAsync(string channel, IReadOnlyList<string> topics, CancellationToken cancellationToken)
    {
        var count = Interlocked.Increment(ref _tokens);
        return TokenFailure is { } failure ? Task.FromException<string>(failure) : Task.FromResult($"jwt-{count}");
    }

    public async Task<HttpResponseMessage> OpenSubscriptionAsync(string token, CancellationToken cancellationToken)
    {
        lock (Subscriptions)
        {
            Subscriptions.Add(cancellationToken);
        }

        var answer = await _answers.Reader.ReadAsync(cancellationToken);
        return answer();
    }
}

// A text/event-stream the test writes as it goes.
internal sealed class SseFeed
{
    private readonly Pipe _pipe = new();

    public HttpResponseMessage Response() => new(HttpStatusCode.OK) { Content = new StreamContent(_pipe.Reader.AsStream()) };

    public Task SendAsync(string json) => WriteAsync($"data: {json}\n\n");

    public async Task WriteAsync(string text) => await _pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(text));

    public Task EndAsync() => _pipe.Writer.CompleteAsync().AsTask();
}
