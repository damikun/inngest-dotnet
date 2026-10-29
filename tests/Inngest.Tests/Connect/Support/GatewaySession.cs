using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;
using Google.Protobuf;
using Inngest.Connect.Protocol;

namespace Inngest.Tests.Connect.Support;

// The gateway's side of one WebSocket. It runs the handshake like the real gateway (GATEWAY_HELLO, then
// GATEWAY_CONNECTION_READY once WORKER_CONNECT arrives), answers heartbeats and acknowledges replies, and records
// every message for the test.
internal sealed class GatewaySession(FakeGateway gateway, WebSocket socket, int number, IReadOnlyList<string> requestedProtocols)
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Lock _lifetime = new();
    private readonly Channel<ConnectMessage> _messages = Channel.CreateUnbounded<ConnectMessage>();
    private readonly ConcurrentQueue<ConnectMessage> _received = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _over;

    public int Number { get; } = number;

    public IReadOnlyList<string> RequestedProtocols { get; } = requestedProtocols;

    public string? AcceptedProtocol => socket.SubProtocol;

    public WorkerConnectRequestData Hello { get; private set; } = null!;

    public IReadOnlyList<ConnectMessage> Received => [.. _received];

    public int TextMessages { get; private set; }

    // How the worker closed the socket, once it has.
    public WebSocketCloseStatus? CloseStatus { get; private set; }

    public string? CloseDescription { get; private set; }

    public Task Closed => _closed.Task;

    // What broke the session, when it was not the socket: a message the worker sent that is not protobuf, for example.
    public Exception? Failure { get; private set; }

    public async Task RunAsync(Func<GatewaySession, ValueTask> publish)
    {
        try
        {
            if (gateway.WithholdHello?.Invoke() == true)
            {
                await ReceiveAsync();
                return;
            }

            await SendAsync(gateway.FirstMessage());
            if (await ReceiveAsync() is not { } connect)
            {
                return;
            }

            _received.Enqueue(connect);
            Hello = WorkerConnectRequestData.Parser.ParseFrom(connect.Payload);
            if (gateway.OnWorkerConnect is { } onConnect && !await onConnect(this))
            {
                await DrainAsync();
                return;
            }

            await SendAsync(
                GatewayMessageType.GatewayConnectionReady,
                new GatewayConnectionReadyData
                {
                    HeartbeatInterval = gateway.HeartbeatInterval,
                    ExtendLeaseInterval = gateway.ExtendLeaseInterval,
                    StatusInterval = "0s",
                });
            await publish(this);

            while (await ReceiveAsync() is { } message)
            {
                _received.Enqueue(message);
                if (message.Kind == GatewayMessageType.WorkerHeartbeat && gateway.AnswerHeartbeats)
                {
                    await SendAsync(GatewayMessageType.GatewayHeartbeat);
                }
                else if (message.Kind == GatewayMessageType.WorkerReply && gateway.AcknowledgeReplies)
                {
                    var reply = SDKResponse.Parser.ParseFrom(message.Payload);
                    await SendAsync(GatewayMessageType.WorkerReplyAck, new WorkerReplyAckData { RequestId = reply.RequestId });
                }

                _messages.Writer.TryWrite(message);
            }
        }
        catch (Exception exception) when (exception is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The worker or the test dropped the socket.
        }
        catch (Exception exception)
        {
            Failure = exception;
        }
        finally
        {
            lock (_lifetime)
            {
                _over = true;
            }

            _messages.Writer.TryComplete();
            _closed.TrySetResult();
        }
    }

    // The next message of this kind from the worker; messages of other kinds before it are skipped.
    public async Task<ConnectMessage> NextAsync(GatewayMessageType kind) =>
        await TryNextAsync(kind, Waits.Timeout)
        ?? throw new TimeoutException(
            $"Session {Number} did not receive {kind}. It received [{string.Join(", ", Received.Select(message => message.Kind))}]" +
            (Closed.IsCompleted ? $" and was closed with {CloseStatus} \"{CloseDescription}\"." : ".") +
            (Failure is null ? string.Empty : $" The session failed: {Failure}"));

    public async Task<T> NextAsync<T>(GatewayMessageType kind, MessageParser<T> parser)
        where T : IMessage<T> => parser.ParseFrom((await NextAsync(kind)).Payload);

    public async Task<ConnectMessage?> TryNextAsync(GatewayMessageType kind, TimeSpan wait)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(wait);
        try
        {
            await foreach (var message in _messages.Reader.ReadAllAsync(timeout.Token))
            {
                if (message.Kind == kind)
                {
                    return message;
                }
            }
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
        }

        return null;
    }

    public async Task SendAsync(GatewayMessageType kind, IMessage? payload = null) =>
        await SendFramesAsync(FrameTestExtensions.Encode(kind, payload), fragments: 1);

    // Sends one message split over several WebSocket frames, as a gateway may for a large request.
    public async Task SendFramesAsync(byte[] message, int fragments)
    {
        await _sendLock.WaitAsync();
        try
        {
            var size = (message.Length + fragments - 1) / Math.Max(fragments, 1);
            for (var i = 0; i < fragments; i++)
            {
                var start = Math.Min(i * size, message.Length);
                var end = i == fragments - 1 ? message.Length : Math.Min(start + size, message.Length);
                await socket.SendAsync(message.AsMemory(start, end - start), WebSocketMessageType.Binary, endOfMessage: i == fragments - 1, CancellationToken.None);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // Closes from the gateway's side, as a gateway that shuts down does. Closed completes once the worker answers.
    public async Task CloseAsync(WebSocketCloseStatus status, string description)
    {
        await _sendLock.WaitAsync();
        try
        {
            await socket.CloseOutputAsync(status, description, CancellationToken.None);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // Drops the connection without a close handshake, as a network failure would. Not once the session is over: its
    // request ends with it, and ASP.NET Core 8 throws when it is aborted by then.
    public void Abort()
    {
        lock (_lifetime)
        {
            if (!_over)
            {
                socket.Abort();
            }
        }
    }

    private async Task DrainAsync()
    {
        while (await ReceiveAsync() is { } message)
        {
            _received.Enqueue(message);
        }
    }

    private async Task<ConnectMessage?> ReceiveAsync()
    {
        var buffer = new ArrayBufferWriter<byte>();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.GetMemory(4096), CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                CloseStatus = socket.CloseStatus;
                CloseDescription = socket.CloseStatusDescription;
                if (socket.State == WebSocketState.CloseReceived)
                {
                    await _sendLock.WaitAsync();
                    try
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                    }
                    finally
                    {
                        _sendLock.Release();
                    }
                }

                return null;
            }

            if (result.MessageType == WebSocketMessageType.Text)
            {
                TextMessages++;
            }

            buffer.Advance(result.Count);
            if (result.EndOfMessage)
            {
                return ConnectMessage.Parser.ParseFrom(buffer.WrittenSpan);
            }
        }
    }
}

internal static class GatewaySessionExtensions
{
    private static int _probes;

    // The worker reads its messages in order, so once it acknowledges a request sent now, it has handled everything
    // sent before. The probe names a function the app does not have and is answered with an error at once.
    public static async Task WaitForWorkerAsync(this GatewaySession session)
    {
        var requestId = $"probe-{Interlocked.Increment(ref _probes)}";
        await session.SendAsync(GatewayMessageType.GatewayExecutorRequest, GatewayMessages.Request(requestId, "shop-probe"));
        while ((await session.NextAsync(GatewayMessageType.WorkerRequestAck, WorkerRequestAckData.Parser)).RequestId != requestId)
        {
        }
    }
}
