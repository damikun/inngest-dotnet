// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Google.Protobuf;
using Inngest.Connect.Protocol;
using Inngest.Errors;
using Inngest.Execution;
using Inngest.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Connect;

// The executions the gateway sends: acknowledging and running them, extending their leases and replying.
internal sealed partial class ConnectWorker
{
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
        try
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
        finally
        {
            await DisposeScopeAsync(scope, data.RequestId).ConfigureAwait(false);
        }
    }

    // The function has run by now. A scoped service that throws while it is disposed must not turn its answer into an
    // error, which Inngest would retry, running its steps again.
    private async ValueTask DisposeScopeAsync(AsyncServiceScope scope, string requestId)
    {
        try
        {
            await scope.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.ScopeDisposalFailed(_logger, requestId, exception);
        }
    }

    private async Task ExtendLeaseAsync(InFlightRequest request)
    {
        var data = request.Data;
        while (await request.LeaseTimer.WaitForNextTickAsync().ConfigureAwait(false))
        {
            // Read first: a renewal between the two reads makes the answer look stale, which only skips it.
            var renewals = request.Renewals;
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

            request.ExtensionSent(renewals);
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

        // Inngest sent the request again with a new lease after this extension went out. Whether the answer grants or
        // refuses the older lease, the newer one is what counts.
        if (!request.AnswerIsCurrent)
        {
            Log.StaleLeaseAnswer(_logger, ack.RequestId);
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
}
