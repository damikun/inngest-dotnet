// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Globalization;
using Inngest.Execution;
using Inngest.Functions;
using Inngest.Internal;
using Inngest.Signing;

namespace Inngest.Serving;

// Calls from Inngest that resume the run of a durable endpoint. They arrive at the endpoint's own URL, with the
// original request's method, query and body, which the signature covers whatever the method. The run's events and
// steps come from the API, and the answer is the usual one: a step as ops, or the endpoint's response once it is done.
internal sealed partial class InngestCommHandler
{
    public async Task<ServeResponse> HandleEndpointCallAsync(IServeRequest request, FunctionDefinition function, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(function);

        SignatureCheck caller;
        if (settings.IsDev)
        {
            caller = DevCaller;
        }
        else if (settings.SigningKey is { } signingKey)
        {
            caller = signer.Verify(request.GetHeader(Headers.Signature), request.Body.Span, signingKey, settings.SigningKeyFallback);
        }
        else
        {
            Log.MissingSigningKey(logger);
            return Respond(request, NoSigningKey, new Reply(500, MisconfiguredBody));
        }

        Reply reply;
        if (!caller.IsValid)
        {
            LogRejected(request, caller);
            reply = Unauthorized;
        }
        else
        {
            try
            {
                reply = await ResumeEndpointAsync(request, function, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                Log.ExecutionFailed(logger, function.Id, exception);
                reply = InternalError(exception);
            }
        }

        try
        {
            return Respond(request, caller, reply);
        }
        catch
        {
            reply.Owner?.Dispose();
            throw;
        }
    }

    private async Task<Reply> ResumeEndpointAsync(IServeRequest request, FunctionDefinition function, CancellationToken cancellationToken)
    {
        var runId = request.GetHeader(EndpointCalls.RunIdHeader)!;
        var forceStepPlan = bool.TryParse(request.GetHeader(EndpointCalls.ForceStepPlanHeader), out var force) && force;
        var execution = new ExecutionRequest
        {
            Function = function,
            Body = EndpointCalls.WriteResumeCall(runId, forceStepPlan),
            StepId = NonEmpty(request.GetHeader(Headers.StepId)),
            Services = request.Services ?? throw new InvalidOperationException("A durable endpoint call needs the request's services."),
            RequestId = NonEmpty(request.GetHeader(Headers.RequestId)),
            JobId = NonEmpty(request.GetHeader(Headers.JobId)),
            GenerationId = int.TryParse(request.GetHeader(Headers.GenerationId), NumberStyles.None, CultureInfo.InvariantCulture, out var generation)
                ? generation
                : null,
        };

        var result = await engine.ExecuteAsync(execution, cancellationToken).ConfigureAwait(false);
        return new Reply(result.StatusCode, result.Body.WrittenMemory, result)
        {
            NoRetry = result.NoRetry,
            RetryAfter = result.RetryAfter,
        };
    }
}
