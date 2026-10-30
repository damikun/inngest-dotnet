using Microsoft.Extensions.AI;

namespace Inngest.Tests.AI;

// A model that answers from a script and keeps what it was asked, standing in for a provider's client.
internal sealed class FakeChatClient(Func<int, IList<ChatMessage>, ChatResponse>? respond = null) : IChatClient
{
    private readonly List<IList<ChatMessage>> _messages = [];
    private readonly List<ChatOptions?> _options = [];
    private int _streams;

    // Called once for each call, counted from zero, with the messages of the call. The default says "Reply 0", "Reply 1", ...
    private Func<int, IList<ChatMessage>, ChatResponse> Respond { get; } = respond ?? ((call, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant, $"Reply {call}")));

    // Throws on the calls it is asked about, counted from zero.
    public Func<int, Exception?>? Fail { get; set; }

    public int CallCount
    {
        get
        {
            lock (_messages)
            {
                return _messages.Count;
            }
        }
    }

    // The calls that streamed, among those counted by CallCount.
    public int StreamCount => Volatile.Read(ref _streams);

    public IReadOnlyList<IList<ChatMessage>> Messages
    {
        get
        {
            lock (_messages)
            {
                return [.. _messages];
            }
        }
    }

    public IReadOnlyList<ChatOptions?> Options
    {
        get
        {
            lock (_messages)
            {
                return [.. _options];
            }
        }
    }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var (call, history) = Record(messages, options);
        if (Fail?.Invoke(call) is { } failure)
        {
            return Task.FromException<ChatResponse>(failure);
        }

        return Task.FromResult(Respond(call, history));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (call, history) = Record(messages, options);
        Interlocked.Increment(ref _streams);
        if (Fail?.Invoke(call) is { } failure)
        {
            throw failure;
        }

        // The text in pieces of a word, as a provider sends it, then the rest of the response in a last update.
        var response = Respond(call, history);
        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents)
            {
                await Task.Yield();
                if (content is TextContent text)
                {
                    foreach (var piece in Pieces(text.Text))
                    {
                        yield return new ChatResponseUpdate(message.Role, piece) { MessageId = message.MessageId ?? "m1" };
                    }
                }
                else
                {
                    yield return new ChatResponseUpdate { Role = message.Role, Contents = [content], MessageId = message.MessageId ?? "m1" };
                }
            }
        }

        yield return new ChatResponseUpdate
        {
            ResponseId = response.ResponseId,
            ModelId = response.ModelId,
            FinishReason = response.FinishReason,
            Contents = response.Usage is { } usage ? [new UsageContent(usage)] : [],
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    private (int Call, IList<ChatMessage> History) Record(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var history = messages.ToList();
        lock (_messages)
        {
            _messages.Add(history);
            _options.Add(options);
            return (_messages.Count - 1, history);
        }
    }

    private static IEnumerable<string> Pieces(string text)
    {
        var words = text.Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            yield return i == words.Length - 1 ? words[i] : words[i] + " ";
        }
    }
}

internal static class AsyncEnumerableExtensions
{
    // IAsyncEnumerable has no ToListAsync before .NET 10.
    public static async Task<List<T>> CollectAsync<T>(this IAsyncEnumerable<T> source)
    {
        List<T> items = [];
        await foreach (var item in source)
        {
            items.Add(item);
        }

        return items;
    }
}
