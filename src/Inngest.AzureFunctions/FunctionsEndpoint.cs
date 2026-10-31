// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.CompilerServices;
using Inngest.AspNetCore;
using Inngest.Internal;
using Inngest.Serving;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Inngest.AzureFunctions;

internal static class FunctionsEndpoint
{
    // Reported to Inngest as the framework of the app.
    public const string Framework = "azure-functions";

    // Where the ASP.NET Core integration of the worker keeps the HttpContext of the request a function runs in; its
    // FunctionContext.GetHttpContext() reads this entry, which a unit test checks against the package. Without the
    // integration there is none.
    public const string HttpContextKey = "HttpRequestContext";

    // The length of a stream sizes the first buffer only up to this much, as in InngestEndpoint, where the length is
    // declared by a caller whose signature has not been checked.
    private const int MaxInitialBufferSize = 64 * 1024;

    // One warning for each app. The handler is a singleton, and the table does not keep it alive.
    private static readonly ConditionalWeakTable<InngestCommHandler, object> StreamingWarned = new();
    private static readonly object Warned = new();

    public static async Task<HttpResponseData> ServeAsync(HttpRequestData request)
    {
        var functionContext = request.FunctionContext;
        var services = functionContext.InstanceServices;
        var handler = services.GetService<InngestCommHandler>()
            ?? throw new InvalidOperationException("Call services.AddInngest() before serving Inngest from a function.");

        // With the ASP.NET Core integration the function runs inside the request of Kestrel, which the host keeps open
        // and forwards as it is written. The call is answered through it, as MapInngest does, so a call can be streamed.
        if (TryGetHttpContext(functionContext, out var httpContext))
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(httpContext.RequestAborted, functionContext.CancellationToken);
            await InngestEndpoint.HandleAsync(httpContext, handler, Framework, linked.Token).ConfigureAwait(false);

            // The answer is on the response already. Returning a response of the request tells the integration that the
            // function has dealt with it, and it adds nothing.
            return request.CreateResponse();
        }

        var cancellationToken = functionContext.CancellationToken;
        using var body = await ReadBodyAsync(request.Body, cancellationToken).ConfigureAwait(false);
        var serveRequest = new FunctionsServeRequest(request, body.Memory);

        // Streams is true for a call from Inngest when streaming is on.
        if (handler.Streams(serveRequest) && StreamingWarned.TryAdd(handler, Warned))
        {
            Log.StreamingNeedsAspNetCoreIntegration(CreateLogger(services));
        }

        using var response = await handler.HandleAsync(serveRequest, cancellationToken).ConfigureAwait(false);

        // The worker keeps this response in memory until the function returns and then hands it to the host.
        var httpResponse = request.CreateResponse((HttpStatusCode)response.StatusCode);
        foreach (var (name, value) in response.Headers)
        {
            // Without validation, since a value such as the RFC 3339 time of a Retry-After is not one that the typed
            // headers of HttpHeaders accept.
            httpResponse.Headers.TryAddWithoutValidation(name, value);
        }

        if (!response.Body.IsEmpty)
        {
            await httpResponse.Body.WriteAsync(response.Body, cancellationToken).ConfigureAwait(false);
        }

        return httpResponse;
    }

    // Qualified, since Inngest.FunctionContext is found first from a namespace inside Inngest.
    private static bool TryGetHttpContext(Microsoft.Azure.Functions.Worker.FunctionContext functionContext, [NotNullWhen(true)] out HttpContext? httpContext)
    {
        if (functionContext.Items.TryGetValue(HttpContextKey, out var value) && value is HttpContext found)
        {
            httpContext = found;
            return true;
        }

        httpContext = null;
        return false;
    }

    private static ILogger CreateLogger(IServiceProvider services) =>
        services.GetService<ILoggerFactory>()?.CreateLogger("Inngest.AzureFunctions") ?? NullLogger.Instance;

    // The built-in model hands over a MemoryStream that holds the whole body already, so its buffer is used as it is.
    // Any other stream is read into a pooled buffer.
    private static async ValueTask<RequestBody> ReadBodyAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (stream is MemoryStream memory && memory.TryGetBuffer(out var segment))
        {
            return new RequestBody(segment.AsMemory(Math.Min((int)memory.Position, segment.Count)), owner: null);
        }

        var expected = stream.CanSeek ? (int)Math.Clamp(stream.Length - stream.Position, 0, MaxInitialBufferSize) : 0;

        // One byte more than expected, so that the read that finds the end does not grow the buffer.
        var buffer = new PooledBufferWriter(expected + 1);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.GetMemory(1), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return new RequestBody(buffer.WrittenMemory, buffer);
                }

                buffer.Advance(read);
            }
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }
}
