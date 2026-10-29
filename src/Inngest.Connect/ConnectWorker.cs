// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;
using Google.Protobuf;
using Inngest.Connect.Protocol;
using Inngest.Errors;
using Inngest.Execution;
using Inngest.Internal;
using Inngest.Registration;
using Inngest.Signing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Inngest.Connect;

// Keeps a connection to an Inngest gateway open, runs the executions the gateway sends over it and answers with their
// results, which are the same as the serve endpoint's answers to HTTP calls.
//
// One loop owns the connections: it opens one, replaces it when it breaks or its gateway drains, and closes it at
// shutdown. The connections' own loops report to it through events and never wait on it. Executions run on the thread
// pool, each with a timer that extends its lease while it runs.
internal sealed class ConnectWorker : BackgroundService, IGatewayListener
{
    private const string GatewayDrainingReason = "GATEWAY_DRAINING";
    private const string ShutdownReason = "WORKER_SHUTDOWN";
    private const string BranchKeyPrefix = "signkey-branch-";

    // The execution version the engine speaks, as in the X-Inngest-Req-Version header of the serve endpoint.
    private const uint ExecutionVersion = 2;

    private readonly InngestSettings _settings;
    private readonly ConnectSettings _connect;
    private readonly IFunctionRegistry _registry;
    private readonly IExecutionEngine _engine;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConnectApi _api;
    private readonly InngestConnection _connection;
    private readonly TimeProvider _time;
    private readonly ILogger<ConnectWorker> _logger;
    private readonly WorkerHello _hello;
    private readonly ReplyBuffer _replies;

    private readonly Channel<WorkerEvent> _events = Channel.CreateUnbounded<WorkerEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, InFlightRequest> _requests = new(StringComparer.Ordinal);
    private readonly InFlightGate _inFlight = new();

    // The token sources hold no timers, so they are left to the collector: teardown can still be using them when
    // the host disposes the worker after giving up on the shutdown.
    // _abort: the host stopped waiting for the shutdown. _stop: the loop has nothing left to wait for.
    // _executions: executions still running when the worker is done are cancelled.
    private readonly CancellationTokenSource _abort = new();
    private readonly CancellationTokenSource _stop;
    private readonly CancellationTokenSource _executions;

    // Only the loop changes these.
    private readonly HashSet<string> _excludedGateways = new(StringComparer.Ordinal);
    private readonly List<Task> _closing = [];
    private GatewayConnection? _active;
    private GatewayConnection? _draining;

    // The key for the next start request and for flushes. A 401 switches between the signing and the fallback key.
    private SigningKey? _key;

    public ConnectWorker(
        InngestSettings settings,
        ConnectSettings connect,
        IFunctionRegistry registry,
        IExecutionEngine engine,
        IServiceScopeFactory scopeFactory,
        ConnectApi api,
        InngestConnection connection,
        TimeProvider time,
        ILogger<ConnectWorker> logger)
    {
        if (!settings.IsDev && settings.SigningKey is null)
        {
            throw new InvalidOperationException(
                "Inngest Connect runs in cloud mode but no signing key is configured. Set INNGEST_SIGNING_KEY, or INNGEST_DEV=1 for local development.");
        }

        if (settings.SigningKey?.Value.StartsWith(BranchKeyPrefix, StringComparison.Ordinal) == true && settings.Environment is null)
        {
            throw new InvalidOperationException(
                "A branch environment signing key needs the name of the environment. Set InngestOptions.Environment or INNGEST_ENV.");
        }

        _settings = settings;
        _connect = connect;
        _registry = registry;
        _engine = engine;
        _scopeFactory = scopeFactory;
        _api = api;
        _connection = connection;
        _time = time;
        _logger = logger;
        _key = settings.SigningKey;
        _hello = new WorkerHello(settings, connect, registry);
        _replies = new ReplyBuffer(api, () => Volatile.Read(ref _key), time, logger);
        _stop = CancellationTokenSource.CreateLinkedTokenSource(_abort.Token);
        _executions = CancellationTokenSource.CreateLinkedTokenSource(_abort.Token);
    }

    // The host cancels this token when its shutdown timeout runs out; executions still running then are cancelled.
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        using (cancellationToken.Register(static state => ((ConnectWorker)state!)._abort.Cancel(), this))
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        // The base class returns as soon as the token is cancelled, which can be before the registration above ran.
        if (cancellationToken.IsCancellationRequested)
        {
            await _abort.CancelAsync().ConfigureAwait(false);
        }
    }

    public override void Dispose()
    {
        _abort.Cancel();
        base.Dispose();
    }

    void IGatewayListener.OnMessage(GatewayConnection connection, GatewayMessageType kind, ReadOnlySpan<byte> payload)
    {
        switch (kind)
        {
            case GatewayMessageType.GatewayExecutorRequest:
                OnExecutorRequest(connection, GatewayExecutorRequestData.Parser.ParseFrom(payload));
                break;
            case GatewayMessageType.WorkerReplyAck:
                _replies.Acknowledge(WorkerReplyAckData.Parser.ParseFrom(payload).RequestId);
                break;
            case GatewayMessageType.WorkerRequestExtendLeaseAck:
                OnLeaseExtended(WorkerRequestExtendLeaseAckData.Parser.ParseFrom(payload));
                break;
            case GatewayMessageType.GatewayClosing:
                _events.Writer.TryWrite(new WorkerEvent(WorkerEventKind.Draining, connection));
                break;
            default:
                Log.UnexpectedMessage(_logger, kind, connection.Id);
                break;
        }
    }

    void IGatewayListener.OnHeartbeatsMissed(GatewayConnection connection) =>
        _events.Writer.TryWrite(new WorkerEvent(WorkerEventKind.HeartbeatsMissed, connection));

    void IGatewayListener.OnClosed(GatewayConnection connection) =>
        _events.Writer.TryWrite(new WorkerEvent(WorkerEventKind.Closed, connection));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var flushing = _replies.RunAsync(_abort.Token);
        using (stoppingToken.Register(static state => ((ConnectWorker)state!).BeginShutdown(), this))
        {
            try
            {
                await RunAsync().ConfigureAwait(false);
            }
            finally
            {
                await TearDownAsync().ConfigureAwait(false);
                _replies.Complete();
                await flushing.ConfigureAwait(false);
                _connection.MoveTo(InngestConnectionState.Closed);
            }
        }
    }

    private async Task RunAsync()
    {
        var token = _stop.Token;
        var failures = 0;
        while (!token.IsCancellationRequested)
        {
            if (Volatile.Read(ref _active) is null)
            {
                // Only a worker that was active reconnects; one that never connected is still connecting.
                _connection.MoveTo(InngestConnectionState.Reconnecting);
                TimeSpan retryIn;
                try
                {
                    Activate(await OpenAsync(token).ConfigureAwait(false));
                    failures = 0;
                    continue;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    retryIn = OnConnectFailed(exception, ++failures);
                }

                try
                {
                    await Task.Delay(retryIn, _time, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            WorkerEvent next;
            try
            {
                next = await _events.Reader.ReadAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await HandleAsync(next).ConfigureAwait(false);
        }
    }

    // A start request for a fresh gateway assignment and session token, then the handshake with that gateway.
    private async Task<GatewayConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var startedAt = _time.GetUtcNow();
        var start = await _api.StartAsync(Volatile.Read(ref _key), _excludedGateways, cancellationToken).ConfigureAwait(false);
        var endpoint = _connect.GatewayUrl ?? GatewayEndpoint(start.GatewayEndpoint);
        try
        {
            return await GatewayConnection
                .OpenAsync(endpoint, start, _hello.Create(start, startedAt), this, _time, _logger, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ConnectException)
        {
            // Inngest is asked for a different gateway next time, as in the TypeScript SDK.
            _excludedGateways.Add(start.GatewayGroup);
            throw;
        }
    }

    private void Activate(GatewayConnection connection)
    {
        _excludedGateways.Remove(connection.GatewayGroup);
        if (Interlocked.Exchange(ref _draining, null) is { } draining)
        {
            // The replacement is up, so the connection to the draining gateway can go.
            Close(draining, GatewayDrainingReason);
        }

        // A full fence between publishing the connection and reading the shutdown flag: either BeginShutdown sees this
        // connection and pauses it, or this sees the shutdown.
        Interlocked.Exchange(ref _active, connection);
        connection.StartHeartbeats();
        connection.Send(_inFlight.IsShuttingDown ? GatewayMessageType.WorkerPause : GatewayMessageType.WorkerReady);
        _connection.MoveTo(InngestConnectionState.Active);
        Log.Connected(_logger, connection.Id, connection.GatewayGroup);
        _replies.RequestFlush();
    }

    private async Task HandleAsync(WorkerEvent next)
    {
        var connection = next.Connection;
        switch (next.Kind)
        {
            case WorkerEventKind.Closed:
                if (connection == Volatile.Read(ref _active))
                {
                    Volatile.Write(ref _active, null);
                    _excludedGateways.Add(connection.GatewayGroup);
                    Log.ConnectionLost(_logger, connection.Id, connection.CloseReason);
                }
                else if (connection == Volatile.Read(ref _draining))
                {
                    Volatile.Write(ref _draining, null);
                }

                await connection.DisposeAsync().ConfigureAwait(false);
                break;

            case WorkerEventKind.Draining when connection == Volatile.Read(ref _active):
                // Executions keep running; their replies and extensions take the replacement once it is up.
                Log.GatewayDraining(_logger, connection.Id);
                connection.StopHeartbeats();
                if (Interlocked.Exchange(ref _draining, connection) is { } older)
                {
                    Close(older, GatewayDrainingReason);
                }

                Volatile.Write(ref _active, null);
                break;

            case WorkerEventKind.HeartbeatsMissed when connection == Volatile.Read(ref _active):
                Log.HeartbeatsMissed(_logger, connection.Id);
                Volatile.Write(ref _active, null);
                _excludedGateways.Add(connection.GatewayGroup);

                // Its Closed event follows, and disposes it.
                connection.Abort();
                break;
        }
    }

    private TimeSpan OnConnectFailed(Exception exception, int failures)
    {
        var failure = (exception as ConnectException)?.Failure;
        var delay = failure == ConnectFailure.GatewayClosing ? Backoff.GatewayDraining() : Backoff.AfterFailures(failures);
        switch (failure)
        {
            case ConnectFailure.Unauthorized when _api.OtherKey(Volatile.Read(ref _key)) is { } other:
                Volatile.Write(ref _key, other);
                Log.SwitchedSigningKey(_logger, other == _settings.SigningKeyFallback ? "fallback" : "primary");
                break;
            case ConnectFailure.ConnectionLimit:
                Log.ConnectionLimitReached(_logger);
                break;
            case ConnectFailure.SyncFailed:
                Log.SyncFailed(_logger, exception.Message);
                break;
            case null:
                Log.ConnectFailedUnexpectedly(_logger, failures, delay, exception);
                return delay;
        }

        Log.ConnectFailed(_logger, failures, delay, exception.Message);
        return delay;
    }

    private void BeginShutdown()
    {
        _connection.MoveTo(InngestConnectionState.Closing);
        var idle = _inFlight.BeginShutdown();
        Log.ShuttingDown(_logger, _inFlight.Count);

        // The gateway stops routing requests here but keeps answering heartbeats, extensions and replies.
        Volatile.Read(ref _active)?.Send(GatewayMessageType.WorkerPause);
        if (idle)
        {
            _stop.Cancel();
        }
    }

    private async Task TearDownAsync()
    {
        // Only executions that lost their lease can still be running here; Inngest has given their work to another
        // worker already.
        await _executions.CancelAsync().ConfigureAwait(false);

        if (Interlocked.Exchange(ref _active, null) is { } active)
        {
            Close(active, ShutdownReason);
        }

        if (Interlocked.Exchange(ref _draining, null) is { } draining)
        {
            Close(draining, ShutdownReason);
        }

        await Task.WhenAll(_closing).ConfigureAwait(false);
        while (_events.Reader.TryRead(out var left))
        {
            await left.Connection.DisposeAsync().ConfigureAwait(false);
        }

        // Replies the gateway never acknowledged can still reach Inngest over HTTP.
        _replies.BufferPending();
        Log.Disconnected(_logger);
    }

    private void Close(GatewayConnection connection, string reason)
    {
        _closing.RemoveAll(static task => task.IsCompleted);
        _closing.Add(connection.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, _abort.Token));
    }

    private void OnExecutorRequest(GatewayConnection connection, GatewayExecutorRequestData data)
    {
        Log.RequestReceived(_logger, data.RequestId, data.FunctionSlug, data.RunId);
        if (data.AppName != _settings.AppId)
        {
            // Without an acknowledgement the gateway gives the request to another worker.
            Log.RequestForOtherApp(_logger, data.RequestId, data.AppName, _settings.AppId);
            return;
        }

        if (_requests.TryGetValue(data.RequestId, out var running))
        {
            AcknowledgeAgain(connection, running, data);
            return;
        }

        if (!_inFlight.TryEnter())
        {
            Log.RequestDuringShutdown(_logger, data.RequestId, data.FunctionSlug);
            return;
        }

        var request = new InFlightRequest(data, connection, _time);
        if (!_requests.TryAdd(data.RequestId, request))
        {
            // The same request came in on the other connection at the same moment.
            Release(request);
            AcknowledgeAgain(connection, _requests.GetValueOrDefault(data.RequestId), data);
            return;
        }

        // Acknowledged before it runs: the gateway waits for this, not for the result.
        Acknowledge(connection, data);
        _ = Task.Run(() => RunAsync(request));
    }

    // Inngest sent a request again that is still running here, for example after its executor restarted. The running
    // execution answers both; it only needs the newest lease.
    private void AcknowledgeAgain(GatewayConnection connection, InFlightRequest? running, GatewayExecutorRequestData data)
    {
        Log.RequestAlreadyRunning(_logger, data.RequestId);
        running?.RenewLease(data.LeaseId);
        Acknowledge(connection, data);
    }

    private static void Acknowledge(GatewayConnection connection, GatewayExecutorRequestData data)
    {
        var ack = new WorkerRequestAckData
        {
            RequestId = data.RequestId,
            AccountId = data.AccountId,
            EnvId = data.EnvId,
            AppId = data.AppId,
            FunctionSlug = data.FunctionSlug,
            SystemTraceCtx = data.SystemTraceCtx,
            UserTraceCtx = data.UserTraceCtx,
            RunId = data.RunId,
        };

        if (data.HasStepId)
        {
            ack.StepId = data.StepId;
        }

        connection.Send(GatewayMessageType.WorkerRequestAck, ack);
    }

    private async Task RunAsync(InFlightRequest request)
    {
        var data = request.Data;
        var extending = ExtendLeaseAsync(request);
        try
        {
            Reply? reply = null;
            try
            {
                reply = await ExecuteAsync(data, _executions.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_executions.IsCancellationRequested)
            {
                Log.ExecutionCancelled(_logger, data.RequestId, data.FunctionSlug);
            }
            catch (Exception exception)
            {
                Log.ExecutionFailed(_logger, data.RequestId, data.FunctionSlug, exception);
                reply = ErrorReply(data, exception);
            }

            // The reply goes out before the request counts as done, so that a shutdown waiting for it closes the
            // connection only after the reply.
            if (reply is not null)
            {
                SendReply(reply);
            }
        }
        finally
        {
            Release(request);
            await extending.ConfigureAwait(false);
        }
    }

    private async Task<Reply> ExecuteAsync(GatewayExecutorRequestData data, CancellationToken cancellationToken)
    {
        using var activity = ConnectTelemetry.StartRequest(data);
        if (!_registry.TryGet(data.FunctionSlug, out var function))
        {
            Log.FunctionNotFound(_logger, data.FunctionSlug);
            return ErrorReply(data, new InvalidOperationException($"Function \"{data.FunctionSlug}\" was not found in this app; is the app synced correctly?"));
        }

        // A scope and a token per execution, as for an HTTP request. A function left waiting on a step never disposes
        // what it registered on its token, and the worker's own token lives as long as the worker does.
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var scope = _scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var execution = new ExecutionRequest
            {
                Function = function,
                Body = data.RequestPayload.Memory,
                StepId = data.HasStepId && data.StepId.Length > 0 ? data.StepId : null,
                Services = scope.ServiceProvider,
                RequestId = data.RequestId.Length > 0 ? data.RequestId : null,
                JobId = data.JobId.Length > 0 ? data.JobId : null,
                DefaultCheckpointMaxRuntime = Checkpointer.ConnectMaxRuntime,
            };

            using var result = await _engine.ExecuteAsync(execution, cancellation.Token).ConfigureAwait(false);
            return CreateReply(data, result.StatusCode, result.Body.WrittenMemory, result.NoRetry ?? false, result.RetryAfter);
        }
    }

    private async Task ExtendLeaseAsync(InFlightRequest request)
    {
        var data = request.Data;
        while (await request.LeaseTimer.WaitForNextTickAsync().ConfigureAwait(false))
        {
            if (request.LeaseId is not { } leaseId)
            {
                return;
            }

            // The active connection if there is one. A draining one is avoided: an extension sent there can succeed
            // while its answer is lost with the connection, which would leave a lease id that is no longer current.
            var connection = Volatile.Read(ref _active) ?? (Volatile.Read(ref _draining) is null ? request.Origin : null);
            var extension = new WorkerRequestExtendLeaseData
            {
                RequestId = data.RequestId,
                AccountId = data.AccountId,
                EnvId = data.EnvId,
                AppId = data.AppId,
                FunctionSlug = data.FunctionSlug,
                SystemTraceCtx = data.SystemTraceCtx,
                UserTraceCtx = data.UserTraceCtx,
                RunId = data.RunId,
                LeaseId = leaseId,
            };

            if (data.HasStepId)
            {
                extension.StepId = data.StepId;
            }

            if (connection is null || !connection.Send(GatewayMessageType.WorkerRequestExtendLease, extension))
            {
                Log.CannotExtendLease(_logger, data.RequestId);
            }
        }
    }

    private void OnLeaseExtended(WorkerRequestExtendLeaseAckData ack)
    {
        // An answer can arrive after the execution is done.
        if (!_requests.TryGetValue(ack.RequestId, out var request))
        {
            return;
        }

        if (ack.HasNewLeaseId && ack.NewLeaseId.Length > 0)
        {
            request.RenewLease(ack.NewLeaseId);
            return;
        }

        // The execution goes on, but Inngest no longer waits for it.
        Log.LeaseLost(_logger, ack.RequestId, request.Data.RunId);
        Release(request);
    }

    private void Release(InFlightRequest request)
    {
        if (!request.Release())
        {
            return;
        }

        _requests.TryRemove(KeyValuePair.Create(request.Data.RequestId, request));
        if (_inFlight.Exit())
        {
            _stop.Cancel();
        }
    }

    private void SendReply(Reply reply)
    {
        if (Volatile.Read(ref _active) is not { } connection)
        {
            Log.ReplyWithoutConnection(_logger, reply.RequestId);
            _replies.Buffer(reply);
            return;
        }

        // The frame holds a copy, so the reply stays intact for a flush whatever happens to the frame.
        var frame = Frames.Create(GatewayMessageType.WorkerReply, reply.Bytes);
        _replies.AddPending(reply);
        if (!connection.Send(frame))
        {
            _replies.Unsent(reply);
        }
    }

    private Reply ErrorReply(GatewayExecutorRequestData data, Exception exception)
    {
        using var body = new PooledBufferWriter();
        using (var writer = WireWriter.Create(body, _settings.SerializerOptions))
        {
            ErrorSerializer.WriteInternal(writer, exception);
        }

        return CreateReply(data, 500, body.WrittenMemory, noRetry: false, retryAfter: null);
    }

    // The answer to an HTTP call, as an SDKResponse. Inngest reads it the same way: 200 is the function's output, 206
    // its steps, anything else an error, which no_retry makes final.
    private static Reply CreateReply(GatewayExecutorRequestData data, int statusCode, ReadOnlyMemory<byte> body, bool noRetry, string? retryAfter)
    {
        var response = new SDKResponse
        {
            RequestId = data.RequestId,
            AccountId = data.AccountId,
            EnvId = data.EnvId,
            AppId = data.AppId,
            RunId = data.RunId,
            Status = statusCode switch
            {
                200 => SDKResponseStatus.Done,
                206 => SDKResponseStatus.NotCompleted,
                _ => SDKResponseStatus.Error,
            },
            Body = UnsafeByteOperations.UnsafeWrap(body),
            NoRetry = noRetry,
            SdkVersion = SdkInfo.Header,
            RequestVersion = ExecutionVersion,

            // Inngest's flush endpoint refuses a reply whose system_trace_ctx is not the JSON it sent.
            SystemTraceCtx = data.SystemTraceCtx,
            UserTraceCtx = data.UserTraceCtx,
        };

        if (retryAfter is not null)
        {
            response.RetryAfter = retryAfter;
        }

        var bytes = new PooledBufferWriter(response.CalculateSize());
        response.WriteTo(bytes);
        return new Reply(data.RequestId, bytes);
    }

    private static Uri GatewayEndpoint(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme is "ws" or "wss"
            ? url
            : throw new ConnectException(ConnectFailure.Rejected, $"Inngest assigned a gateway at '{text}', which is not a ws or wss URL.");

    private enum WorkerEventKind
    {
        Closed,
        Draining,
        HeartbeatsMissed,
    }

    private readonly record struct WorkerEvent(WorkerEventKind Kind, GatewayConnection Connection);
}
