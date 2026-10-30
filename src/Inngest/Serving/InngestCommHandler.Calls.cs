// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Globalization;
using Inngest.Execution;
using Inngest.Functions;
using Inngest.Internal;
using Inngest.Signing;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Serving;

// Calls that run a function or one of its steps.
internal sealed partial class InngestCommHandler
{
    private async Task<Reply> CallAsync(IServeRequest request, SignatureCheck caller, CancellationToken cancellationToken)
    {
        // Checked before the signature, as in the TypeScript SDK.
        if (request.Body.IsEmpty)
        {
            Log.MissingBody(logger);
            return Unauthorized;
        }

        if (!caller.IsValid)
        {
            LogRejected(request, caller);
            return Unauthorized;
        }

        var probe = request.GetQuery(ProbeParameter);
        if (!string.IsNullOrEmpty(probe))
        {
            // The trust probe only checks that the app answers with a valid signature.
            if (probe == TrustProbe)
            {
                return new Reply(200, ReadOnlyMemory<byte>.Empty);
            }

            Log.UnknownProbe(logger, probe);
            return Error(400, type: null, $"Unknown probe \"{probe}\"");
        }

        var functionId = request.GetQuery(FunctionIdParameter);
        if (string.IsNullOrEmpty(functionId))
        {
            return Error(500, "internal", "No function ID found in request");
        }

        if (!registry.TryGet(functionId, out var function))
        {
            Log.FunctionNotFound(logger, functionId);
            return Error(500, "internal", $"Function \"{functionId}\" was not found in this app; is the app synced correctly?");
        }

        var stepId = request.GetQuery(StepIdParameter);
        if (string.IsNullOrEmpty(stepId))
        {
            stepId = request.GetHeader(Headers.StepId) is { Length: > 0 } header ? header : null;
        }

        try
        {
            if (request.Services is { } services)
            {
                return await ExecuteAsync(request, function, stepId, services, cancellationToken).ConfigureAwait(false);
            }

            // Hosts without a request scope get one per invocation, so scoped services work as in a web request.
            var scope = scopeFactory.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                return await ExecuteAsync(request, function, stepId, scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            Log.ExecutionFailed(logger, functionId, exception);
            return InternalError(exception);
        }
    }

    private async Task<Reply> ExecuteAsync(
        IServeRequest request,
        FunctionDefinition function,
        string? stepId,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var execution = new ExecutionRequest
        {
            Function = function,
            Body = request.Body,
            StepId = stepId,
            Services = services,
            RequestId = NonEmpty(request.GetHeader(Headers.RequestId)),
            JobId = NonEmpty(request.GetHeader(Headers.JobId)),
            GenerationId = int.TryParse(request.GetHeader(Headers.GenerationId), NumberStyles.None, CultureInfo.InvariantCulture, out var generation)
                ? generation
                : null,
        };

        var result = await engine.ExecuteAsync(execution, cancellationToken).ConfigureAwait(false);

        // The response takes over the result, which owns the body.
        return new Reply(result.StatusCode, result.Body.WrittenMemory, result)
        {
            NoRetry = result.NoRetry,
            RetryAfter = result.RetryAfter,
        };
    }
}
