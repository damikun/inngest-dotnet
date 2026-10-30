// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Inngest.AwsLambda;
using Inngest.Serving;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest;

/// <summary>
/// Serves the Inngest endpoint from an AWS Lambda function: GET for introspection, PUT to sync the app and POST to run
/// functions. Build one from the service provider of an app that called <c>AddInngest</c> when the function starts, and
/// pass it each event through the overload for the event source: REST APIs of API Gateway send payload format 1.0, HTTP
/// APIs and Lambda function URLs send payload format 2.0.
/// </summary>
/// <remarks>
/// Each invocation runs in a service scope of its own. The cancellation token that functions receive is never
/// cancelled, since Lambda gives an invocation no signal before it ends one that runs out of time. The response is
/// buffered, so <see cref="InngestOptions.Streaming"/> has no effect.
/// </remarks>
public sealed class InngestLambdaHandler
{
    private readonly InngestCommHandler _handler;

    /// <summary>
    /// Takes the SDK from <paramref name="services"/>, which must be built from a collection that called
    /// <c>AddInngest</c>.
    /// </summary>
    public InngestLambdaHandler(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Resolving here fails at startup, not on the first invocation, when AddInngest was not called.
        _handler = services.GetService<InngestCommHandler>()
            ?? throw new InvalidOperationException("Call services.AddInngest() before creating the Lambda handler.");
    }

    /// <summary>
    /// Answers an event of an API Gateway REST API, which uses payload format 1.0.
    /// </summary>
    public async Task<APIGatewayProxyResponse> HandleAsync(APIGatewayProxyRequest request, ILambdaContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var body = LambdaBody.Decode(request.Body, request.IsBase64Encoded);
        var serveRequest = LambdaServeRequest.Create(request, body?.WrittenMemory ?? default);
        using var response = await _handler.HandleAsync(serveRequest, CancellationToken.None).ConfigureAwait(false);
        return LambdaResponses.ToRestApi(response);
    }

    /// <summary>
    /// Answers an event of an API Gateway HTTP API or a Lambda function URL, which use payload format 2.0.
    /// </summary>
    public async Task<APIGatewayHttpApiV2ProxyResponse> HandleAsync(APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var body = LambdaBody.Decode(request.Body, request.IsBase64Encoded);
        var serveRequest = LambdaServeRequest.Create(request, body?.WrittenMemory ?? default);
        using var response = await _handler.HandleAsync(serveRequest, CancellationToken.None).ConfigureAwait(false);
        return LambdaResponses.ToHttpApi(response);
    }
}
