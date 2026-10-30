// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Collections.Concurrent;
using System.Threading.Channels;
using Inngest.Configuration;
using Inngest.Connect.Protocol;
using Inngest.Execution;
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
internal sealed partial class ConnectWorker : BackgroundService, IGatewayListener
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
                try
                {
                    await TearDownAsync().ConfigureAwait(false);
                    _replies.Complete();
                    await flushing.ConfigureAwait(false);
                }
                finally
                {
                    // However the teardown went, the worker is done; otherwise the health check would report it closing forever.
                    _connection.MoveTo(InngestConnectionState.Closed);
                }
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
