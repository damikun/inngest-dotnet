// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Net.WebSockets;
using Inngest.Connect.Protocol;

namespace Inngest.Connect;

// Opening, replacing and closing gateway connections, and shutting the worker down.
internal sealed partial class ConnectWorker
{
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
}
