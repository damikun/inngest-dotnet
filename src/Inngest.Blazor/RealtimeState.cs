// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Collections;
using System.Text;

namespace Inngest;

/// <summary>
/// What a RealtimeView has received so far and how its subscription stands, handed to the view's content on every
/// render. The view changes it only on the renderer's thread, so read it while rendering or in an event handler, and
/// copy what you keep.
/// </summary>
/// <typeparam name="TData">The type of the data of the messages.</typeparam>
public sealed class RealtimeState<TData>
{
    // Inngest streams up to 1,000 chunks of 1 KB, which is where the text of a stream stops growing.
    private const int MaxTextLength = 1 << 20;

    private readonly MessageBuffer _messages = new();
    private readonly Dictionary<string, RealtimeMessage<TData>> _latestByTopic = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StringBuilder> _text = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, string>? _textByTopic;

    internal RealtimeState()
    {
    }

    /// <summary>Where the subscription stands.</summary>
    public RealtimeConnectionState Connection { get; private set; }

    /// <summary>Why the subscription failed, while Connection is Failed.</summary>
    public Exception? Error { get; private set; }

    /// <summary>The newest messages, the oldest first, at most MaxMessages of them.</summary>
    public IReadOnlyList<RealtimeMessage<TData>> Messages => _messages;

    /// <summary>The newest message of any topic, or null before the first.</summary>
    public RealtimeMessage<TData>? Latest { get; private set; }

    /// <summary>The newest message of each topic that has sent one, a chunk or the end of a stream included.</summary>
    public IReadOnlyDictionary<string, RealtimeMessage<TData>> LatestByTopic => _latestByTopic;

    /// <summary>
    /// The text of the newest stream of each topic that has streamed one, which grows with every chunk and starts over
    /// with the next stream. Unlike Messages it does not forget old chunks.
    /// </summary>
    public IReadOnlyDictionary<string, string> TextByTopic =>
        _textByTopic ??= _text.ToDictionary(static pair => pair.Key, static pair => pair.Value.ToString(), StringComparer.Ordinal);

    internal int Capacity
    {
        set => _messages.Capacity = value;
    }

    // A new subscription starts a new timeline.
    internal void Begin(RealtimeConnectionState connection)
    {
        _messages.Clear();
        _latestByTopic.Clear();
        _text.Clear();
        _textByTopic = null;
        Latest = null;
        Error = null;
        Connection = connection;
    }

    internal void Report(RealtimeConnectionState connection)
    {
        // A failure is the last word of a subscription, and a late report of its connection must not take it back.
        if (Connection is not (RealtimeConnectionState.Failed or RealtimeConnectionState.Closed))
        {
            Connection = connection;
        }
    }

    internal void Fail(Exception error)
    {
        Error = error;
        Connection = RealtimeConnectionState.Failed;
    }

    internal void Add(RealtimeMessage<TData> message)
    {
        // A message proves the connection is up, which a source that cannot tell has not said.
        if (Connection is RealtimeConnectionState.Connecting or RealtimeConnectionState.Reconnecting)
        {
            Connection = RealtimeConnectionState.Open;
        }

        _messages.Add(message);
        Latest = message;
        if (message.Topic.Length == 0)
        {
            return;
        }

        _latestByTopic[message.Topic] = message;
        switch (message.Kind)
        {
            case RealtimeMessageKind.StreamStart:
                _text[message.Topic] = new StringBuilder();
                _textByTopic = null;
                break;
            case RealtimeMessageKind.StreamChunk when message.Text is { Length: > 0 } chunk:
                if (!_text.TryGetValue(message.Topic, out var text))
                {
                    _text[message.Topic] = text = new StringBuilder();
                }

                if (text.Length + chunk.Length <= MaxTextLength)
                {
                    text.Append(chunk);
                    _textByTopic = null;
                }

                break;
            default:
                break;
        }
    }

    // A ring over a list: it grows to the capacity, then overwrites the oldest message.
    private sealed class MessageBuffer : IReadOnlyList<RealtimeMessage<TData>>
    {
        private readonly List<RealtimeMessage<TData>> _items = [];
        private int _capacity = int.MaxValue;
        private int _oldest;

        public int Capacity
        {
            set
            {
                if (value == _capacity)
                {
                    return;
                }

                // Keeps the newest ones in order, with the oldest first again.
                var kept = _items.Count > value ? _items.Count - value : 0;
                var ordered = new List<RealtimeMessage<TData>>(_items.Count - kept);
                for (var i = kept; i < _items.Count; i++)
                {
                    ordered.Add(this[i]);
                }

                _items.Clear();
                _items.AddRange(ordered);
                _oldest = 0;
                _capacity = value;
            }
        }

        public int Count => _items.Count;

        public RealtimeMessage<TData> this[int index] =>
            (uint)index < (uint)_items.Count ? _items[(_oldest + index) % _items.Count] : throw new ArgumentOutOfRangeException(nameof(index));

        public void Add(RealtimeMessage<TData> message)
        {
            if (_capacity == 0)
            {
                return;
            }

            if (_items.Count < _capacity)
            {
                _items.Add(message);
                return;
            }

            _items[_oldest] = message;
            _oldest = (_oldest + 1) % _items.Count;
        }

        public void Clear()
        {
            _items.Clear();
            _oldest = 0;
        }

        public IEnumerator<RealtimeMessage<TData>> GetEnumerator()
        {
            for (var i = 0; i < _items.Count; i++)
            {
                yield return this[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
