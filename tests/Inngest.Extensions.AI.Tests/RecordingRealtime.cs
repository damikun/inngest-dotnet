using System.Text.Json;

namespace Inngest.Extensions.AI.Tests;

// IInngestRealtime that takes a stream of text and keeps the chunks it was given, one by one.
internal sealed class RecordingRealtime : IInngestRealtime
{
    private readonly object _gate = new();
    private readonly List<string> _chunks = [];
    private readonly List<(int Count, TaskCompletionSource Reached)> _waiters = [];

    // Thrown by PublishStreamAsync before it reads a chunk, as a request that Inngest refuses.
    public Exception? Fail { get; init; }

    public IReadOnlyList<string> Chunks
    {
        get
        {
            lock (_gate)
            {
                return [.. _chunks];
            }
        }
    }

    // Completes once this many chunks have been read in total.
    public Task WaitForChunksAsync(int count)
    {
        lock (_gate)
        {
            if (_chunks.Count >= count)
            {
                return Task.CompletedTask;
            }

            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((count, reached));
            return reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    public async Task<string> PublishStreamAsync(RealtimeTopic topic, IAsyncEnumerable<string> chunks, CancellationToken cancellationToken = default)
    {
        if (Fail is not null)
        {
            await Task.Yield();
            throw Fail;
        }

        await foreach (var chunk in chunks.WithCancellation(cancellationToken))
        {
            lock (_gate)
            {
                _chunks.Add(chunk);
                foreach (var waiter in _waiters.Where(waiter => waiter.Count <= _chunks.Count))
                {
                    waiter.Reached.TrySetResult();
                }
            }
        }

        return string.Concat(Chunks);
    }

    public Task PublishAsync<T>(RealtimeTopic topic, T data, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task PublishAsync<T>(RealtimeTopic<T> topic, T data, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<RealtimeToken> GetSubscriptionTokenAsync(string channel, IReadOnlyList<string> topics, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public IAsyncEnumerable<RealtimeMessage<JsonElement>> SubscribeAsync(string channel, IReadOnlyList<string> topics, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public IAsyncEnumerable<RealtimeMessage<T>> SubscribeAsync<T>(RealtimeTopic<T> topic, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
