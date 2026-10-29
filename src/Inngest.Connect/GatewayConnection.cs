// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Net.WebSockets;
using System.Threading.Channels;
using Google.Protobuf;
using Inngest.Connect.Protocol;
using Inngest.Internal;
using Microsoft.Extensions.Logging;

namespace Inngest.Connect;

// What a connection reports to the worker that owns it. Calls come from the connection's own loops and must return
// quickly: the next message is not read until OnMessage returns.
internal interface IGatewayListener
{
    void OnMessage(GatewayConnection connection, GatewayMessageType kind, ReadOnlySpan<byte> payload);

    void OnHeartbeatsMissed(GatewayConnection connection);

    void OnClosed(GatewayConnection connection);
}

// One WebSocket to a gateway, from the handshake to the close. A WebSocket allows one send at a time, so every frame
// goes through a queue with a single writer, the close included. A single read loop hands each message to the
// listener as it arrives.
internal sealed class GatewayConnection : IAsyncDisposable
{
    public const string SubProtocol = "v0.connect.inngest.com";

    // The code and reason the TypeScript SDK closes a failed handshake with.
    private const WebSocketCloseStatus HandshakeFailedStatus = (WebSocketCloseStatus)4001;
    private const string HandshakeFailedReason = "UNEXPECTED";

    // Requests stay under about 4 MB; anything far larger is not a gateway message.
    private const int MaxMessageSize = 16 * 1024 * 1024;
    private const int ReceiveBufferSize = 16 * 1024;
    private const int RetainedBufferSize = 1024 * 1024;
    private const int MaxUnansweredHeartbeats = 2;

    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);

    // The gateway syncs the app before it answers WORKER_CONNECT, which takes a while the first time.
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    // What the TypeScript SDK assumes when the gateway leaves an interval out.
    private static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultExtendLeaseInterval = TimeSpan.FromSeconds(5);

    private readonly ClientWebSocket _socket;
    private readonly Channel<Outbound> _outbound = Channel.CreateUnbounded<Outbound>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private IGatewayListener? _listener;
    private Task _reading = Task.CompletedTask;
    private Task _writing = Task.CompletedTask;
    private Task _heartbeating = Task.CompletedTask;
    private PeriodicTimer? _heartbeatTimer;
    private int _unansweredHeartbeats;
    private int _disposed;

    private GatewayConnection(ClientWebSocket socket, StartResponse start, TimeProvider time, ILogger logger)
    {
        _socket = socket;
        _time = time;
        _logger = logger;
        Id = start.ConnectionId;
        GatewayGroup = start.GatewayGroup;
    }

    public string Id { get; }

    public string GatewayGroup { get; }

    public TimeSpan HeartbeatInterval { get; private set; } = DefaultHeartbeatInterval;

    public TimeSpan ExtendLeaseInterval { get; private set; } = DefaultExtendLeaseInterval;

    // Completes when the connection has ended, after which CloseReason says why.
    public Task Closed => _closed.Task;

    public string CloseReason { get; private set; } = "the connection is open";

    // Dials the gateway and runs the handshake: GATEWAY_HELLO, WORKER_CONNECT, GATEWAY_CONNECTION_READY. Returns a
    // connection that reads and writes on its own; throws ConnectException when the gateway does not get that far.
    public static async Task<GatewayConnection> OpenAsync(
        Uri endpoint,
        StartResponse start,
        WorkerConnectRequestData hello,
        IGatewayListener listener,
        TimeProvider time,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(SubProtocol);
        var connection = new GatewayConnection(socket, start, time, logger);
        try
        {
            await connection.HandshakeAsync(endpoint, hello, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await connection.AbandonAsync().ConfigureAwait(false);
            throw;
        }

        connection._listener = listener;
        connection._writing = connection.WriteLoopAsync();
        connection._reading = connection.ReadLoopAsync();
        return connection;
    }

    // Queues a frame. Returns false once the connection is closing, when nothing more goes out.
    public bool Send(GatewayMessageType kind, IMessage? payload = null) => Enqueue(Frames.Create(kind, payload));

    public bool Send(GatewayMessageType kind, ReadOnlyMemory<byte> payload) => Enqueue(Frames.Create(kind, payload));

    // Takes over the frame, which is returned to the pool once sent or dropped.
    public bool Send(Frame frame) => Enqueue(frame);

    public void StartHeartbeats()
    {
        var timer = new PeriodicTimer(HeartbeatInterval, _time);
        if (Interlocked.CompareExchange(ref _heartbeatTimer, timer, null) is not null)
        {
            timer.Dispose();
            return;
        }

        _heartbeating = HeartbeatAsync(timer);
    }

    public void StopHeartbeats() => Interlocked.Exchange(ref _heartbeatTimer, null)?.Dispose();

    // Queues the close behind the frames already waiting, so that they still go out, and accepts nothing after it.
    public void Close(WebSocketCloseStatus status, string reason)
    {
        StopHeartbeats();
        if (_outbound.Writer.TryWrite(new Outbound(null, status, reason)))
        {
            _outbound.Writer.TryComplete();
        }
    }

    // Closes, waits a moment for the gateway to answer the close, and releases the socket.
    public async Task CloseAsync(WebSocketCloseStatus status, string reason, CancellationToken cancellationToken)
    {
        Close(status, reason);
        await Closed.WaitAsync(CloseTimeout, _time, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await DisposeAsync().ConfigureAwait(false);
    }

    // Drops the connection at once, for when the gateway stopped answering.
    public void Abort()
    {
        StopHeartbeats();
        _outbound.Writer.TryComplete();
        _socket.Abort();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Frames still queued, and a close among them, get a moment to go out before the socket goes away.
        _outbound.Writer.TryComplete();
        await _writing.WaitAsync(CloseTimeout, _time).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        Abort();
        await Task.WhenAll(_reading, _writing, _heartbeating).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _socket.Dispose();
    }

    private async Task HandshakeAsync(Uri endpoint, WorkerConnectRequestData hello, CancellationToken cancellationToken)
    {
        using var buffer = new PooledBufferWriter(ReceiveBufferSize);
        using (var timeout = new CancellationTokenSource(HelloTimeout, _time))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token))
        {
            try
            {
                await _socket.ConnectAsync(endpoint, linked.Token).ConfigureAwait(false);
                var kind = await ReceiveHandshakeAsync(buffer, linked.Token).ConfigureAwait(false);
                if (kind != GatewayMessageType.GatewayHello)
                {
                    throw new ConnectException(ConnectFailure.Rejected, $"The gateway at {endpoint} sent {kind} instead of GATEWAY_HELLO.");
                }
            }
            catch (OperationCanceledException exception) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new ConnectException(ConnectFailure.Unreachable, $"The gateway at {endpoint} did not answer within {HelloTimeout.TotalSeconds:0} seconds.", exception);
            }
            catch (Exception exception) when (exception is WebSocketException or IOException)
            {
                throw new ConnectException(ConnectFailure.Unreachable, $"Could not open a WebSocket to the gateway at {endpoint}: {exception.Message}", exception);
            }
        }

        using (var timeout = new CancellationTokenSource(ReadyTimeout, _time))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token))
        {
            GatewayMessageType kind;
            try
            {
                using (var frame = Frames.Create(GatewayMessageType.WorkerConnect, hello))
                {
                    await _socket.SendAsync(frame.Memory, WebSocketMessageType.Binary, endOfMessage: true, linked.Token).ConfigureAwait(false);
                }

                kind = await ReceiveHandshakeAsync(buffer, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new ConnectException(ConnectFailure.Unreachable, $"The gateway did not confirm connection {Id} within {ReadyTimeout.TotalSeconds:0} seconds.", exception);
            }
            catch (Exception exception) when (exception is WebSocketException or IOException)
            {
                throw new ConnectException(ConnectFailure.Unreachable, $"Connection {Id} broke during the handshake: {exception.Message}", exception);
            }

            Frames.TryRead(buffer.WrittenSpan, out _, out var payload);
            switch (kind)
            {
                case GatewayMessageType.GatewayConnectionReady:
                    var ready = Parse(GatewayConnectionReadyData.Parser, payload);
                    HeartbeatInterval = Interval(ready.HeartbeatInterval, DefaultHeartbeatInterval);
                    ExtendLeaseInterval = Interval(ready.ExtendLeaseInterval, DefaultExtendLeaseInterval);
                    break;
                case GatewayMessageType.SyncFailed:
                    var error = Parse(SystemError.Parser, payload);
                    throw new ConnectException(ConnectFailure.SyncFailed, $"Inngest could not sync the app ({error.Code}): {error.Message}");
                default:
                    throw new ConnectException(ConnectFailure.Rejected, $"The gateway sent {kind} instead of GATEWAY_CONNECTION_READY.");
            }
        }
    }

    private static T Parse<T>(MessageParser<T> parser, ReadOnlySpan<byte> payload)
        where T : IMessage<T>
    {
        try
        {
            return parser.ParseFrom(payload);
        }
        catch (InvalidProtocolBufferException exception)
        {
            throw new ConnectException(ConnectFailure.Rejected, $"The gateway sent a {typeof(T).Name} that cannot be read.", exception);
        }
    }

    // Reads one message of the handshake. A close instead fails the attempt with the gateway's reason, which for a
    // draining gateway is connect_gateway_closing.
    private async Task<GatewayMessageType> ReceiveHandshakeAsync(PooledBufferWriter buffer, CancellationToken cancellationToken)
    {
        if (!await ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false))
        {
            var reason = DescribeClose();
            throw new ConnectException(
                _socket.CloseStatusDescription == "connect_gateway_closing" ? ConnectFailure.GatewayClosing : ConnectFailure.Rejected,
                $"The gateway closed connection {Id} during the handshake: {reason}.");
        }

        if (!Frames.TryRead(buffer.WrittenSpan, out var kind, out _))
        {
            throw new ConnectException(ConnectFailure.Rejected, "The gateway sent a message that is not a ConnectMessage.");
        }

        return kind;
    }

    // Reads one whole message, which may arrive in several frames, into the buffer. Returns false when the gateway
    // closed the connection instead.
    private async ValueTask<bool> ReceiveAsync(PooledBufferWriter buffer, CancellationToken cancellationToken)
    {
        buffer.Clear();
        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer.GetMemory(ReceiveBufferSize), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return false;
            }

            buffer.Advance(result.Count);
            if (buffer.WrittenCount > MaxMessageSize)
            {
                throw new WebSocketException(WebSocketError.Faulted, $"The gateway sent a message of more than {MaxMessageSize / (1024 * 1024)} MB.");
            }

            if (result.EndOfMessage)
            {
                return true;
            }
        }
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new PooledBufferWriter(ReceiveBufferSize);
        try
        {
            while (await ReceiveAsync(buffer, CancellationToken.None).ConfigureAwait(false))
            {
                Dispatch(buffer.WrittenSpan);
                if (buffer.WrittenCount > RetainedBufferSize)
                {
                    // A large request should not keep its buffer out of the pool for the life of the connection.
                    buffer.Dispose();
                    buffer = new PooledBufferWriter(ReceiveBufferSize);
                }
            }

            CloseReason = $"closed with {DescribeClose()}";
            if (_socket.State == WebSocketState.CloseReceived)
            {
                // The close handshake needs an answer.
                Close(_socket.CloseStatus ?? WebSocketCloseStatus.NormalClosure, string.Empty);
            }
        }
        catch (Exception exception)
        {
            CloseReason = _socket.State == WebSocketState.Aborted && Volatile.Read(ref _disposed) == 1
                ? "the worker dropped it"
                : $"it failed: {exception.Message}";
            if (exception is not (WebSocketException or IOException or OperationCanceledException or ObjectDisposedException))
            {
                Log.ReadLoopFailed(_logger, Id, exception);
            }
        }
        finally
        {
            buffer.Dispose();
            StopHeartbeats();
            _outbound.Writer.TryComplete();
            _closed.TrySetResult();
            _listener!.OnClosed(this);
        }
    }

    private void Dispatch(ReadOnlySpan<byte> frame)
    {
        if (!Frames.TryRead(frame, out var kind, out var payload))
        {
            Log.InvalidMessage(_logger, Id, frame.Length);
            return;
        }

        if (kind == GatewayMessageType.GatewayHeartbeat)
        {
            Volatile.Write(ref _unansweredHeartbeats, 0);
            return;
        }

        try
        {
            _listener!.OnMessage(this, kind, payload);
        }
        catch (InvalidProtocolBufferException exception)
        {
            Log.InvalidPayload(_logger, kind, Id, exception);
        }
    }

    private async Task WriteLoopAsync()
    {
        var reader = _outbound.Reader;
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var message))
                {
                    using (message.Frame)
                    {
                        if (message.Frame is { } frame)
                        {
                            await _socket.SendAsync(frame.Memory, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None).ConfigureAwait(false);
                            continue;
                        }

                        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                        {
                            await _socket.CloseOutputAsync(message.CloseStatus, message.CloseReason, CancellationToken.None).ConfigureAwait(false);
                        }

                        return;
                    }
                }
            }
        }
        catch (Exception exception)
        {
            // A failed send leaves the socket unusable; aborting it ends the read loop too.
            if (exception is not (WebSocketException or IOException or OperationCanceledException or ObjectDisposedException))
            {
                Log.WriteLoopFailed(_logger, Id, exception);
            }

            Abort();
        }
        finally
        {
            _outbound.Writer.TryComplete();
            while (reader.TryRead(out var left))
            {
                left.Frame?.Dispose();
            }
        }
    }

    private async Task HeartbeatAsync(PeriodicTimer timer)
    {
        while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
        {
            // The gateway answers every heartbeat. Two in a row without an answer mean the connection is gone, even
            // when the socket has not noticed yet.
            if (Interlocked.Increment(ref _unansweredHeartbeats) > MaxUnansweredHeartbeats)
            {
                StopHeartbeats();
                _listener?.OnHeartbeatsMissed(this);
                return;
            }

            Send(GatewayMessageType.WorkerHeartbeat);
        }
    }

    private bool Enqueue(Frame frame)
    {
        if (_outbound.Writer.TryWrite(new Outbound(frame)))
        {
            return true;
        }

        frame.Dispose();
        return false;
    }

    private async Task AbandonAsync()
    {
        Volatile.Write(ref _disposed, 1);
        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                using var timeout = new CancellationTokenSource(CloseTimeout, _time);
                await _socket.CloseOutputAsync(HandshakeFailedStatus, HandshakeFailedReason, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is WebSocketException or IOException or OperationCanceledException)
            {
                // The attempt has failed already; the close only tells the gateway.
            }
        }

        _socket.Dispose();
    }

    private string DescribeClose() =>
        _socket.CloseStatus is { } status
            ? string.IsNullOrEmpty(_socket.CloseStatusDescription) ? $"{(int)status}" : $"{(int)status} {_socket.CloseStatusDescription}"
            : "no close status";

    // A timer cannot tick faster than once a millisecond, and no gateway asks for that.
    private static TimeSpan Interval(string value, TimeSpan fallback) =>
        GoDuration.TryParse(value, out var interval) && interval >= TimeSpan.FromMilliseconds(1) ? interval : fallback;

    // A frame to send, or with no frame, the close that ends the connection.
    private readonly record struct Outbound(Frame? Frame, WebSocketCloseStatus CloseStatus = default, string? CloseReason = null);
}
