// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Buffers;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Inngest.Configuration;
using Inngest.Execution;
using Inngest.Functions;
using Inngest.Internal;
using Inngest.Serving;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;

namespace Inngest.AspNetCore;

// Runs the requests to durable endpoints as Inngest runs. A request from a client starts a run: its steps run inline,
// and the client gets the endpoint's response, or a redirect to wait for it once Inngest has taken the run over. A call
// from Inngest resumes a run at the endpoint's own URL and gets the usual answer: a step as ops, or the response.
internal sealed class DurableEndpointMiddleware
{
    // How much of a finished endpoint's response its run records. The client always gets all of it, and the response
    // to a resumed run is recorded whole, because the client waiting for the run gets it from the record.
    private const int MaxRecordedResponseBody = 1024 * 1024;

    // Inngest replays the recorded headers to the client waiting for a run, which gets a body of its own length.
    private static readonly HashSet<string> UnrecordedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Length",
        "Transfer-Encoding",
        "Connection",
        "Keep-Alive",
    };

    private readonly RequestDelegate _next;
    private readonly IExecutionEngine _engine;
    private readonly InngestCommHandler _handler;
    private readonly InngestSettings _settings;
    private readonly TimeProvider _time;
    private readonly CancellationToken _stopping;
    private readonly ILogger _logger;

    public DurableEndpointMiddleware(RequestDelegate next, IServiceProvider services)
    {
        _next = next;
        _engine = services.GetService<IExecutionEngine>()
            ?? throw new InvalidOperationException("Call services.AddInngest() before app.UseInngestDurableEndpoints().");
        _handler = services.GetRequiredService<InngestCommHandler>();
        _settings = services.GetRequiredService<InngestSettings>();
        _time = services.GetService<TimeProvider>() ?? TimeProvider.System;
        _stopping = services.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? CancellationToken.None;
        _logger = (services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance).CreateLogger("Inngest.AspNetCore.DurableEndpoints");
    }

    public Task InvokeAsync(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        return endpoint?.Metadata.GetMetadata<IDurableEndpointMetadata>() is { } metadata
            ? RunAsync(context, endpoint, metadata.Options)
            : _next(context);
    }

    private async Task RunAsync(HttpContext context, Endpoint endpoint, DurableEndpointOptions options)
    {
        var cancellationToken = context.RequestAborted;
        var functionId = options.Id ?? DefaultId(context, endpoint);
        var body = await ReadBodyAsync(context, options.MaxRequestBodySize, cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            Log.EndpointBodyTooLarge(_logger, functionId, options.MaxRequestBodySize);
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        // The endpoint reads the body from memory, as often as it likes.
        context.Request.Body = new MemoryStream(body, writable: false);
        var request = new HttpServeRequest(context, body);
        if (EndpointCalls.IsResume(request, _settings.IsDev))
        {
            await ResumeAsync(context, request, functionId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await StartAsync(context, body, functionId, options, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task StartAsync(HttpContext context, byte[] body, string functionId, DurableEndpointOptions options, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var runId = Ulid.New(now);
        var retries = options.RetriesOrDefault;
        var @event = EndpointCalls.WriteEvent(Describe(context, body, functionId), now);

        // What middleware before this one has set, such as security headers. An answer that replaces the endpoint's
        // response keeps them and drops what the endpoint set.
        KeyValuePair<string, StringValues>[] earlierHeaders = [.. context.Response.Headers];
        var capture = new CapturedResponse(context);
        ExecutionResult result;
        try
        {
            result = await _engine.ExecuteAsync(
                new ExecutionRequest
                {
                    Function = Function(functionId, context, capture, resumed: false),
                    Body = EndpointCalls.WriteStartCall(@event, runId, retries),
                    Services = context.RequestServices,
                    Endpoint = new EndpointCall(@event, retries),
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            capture.Detach();
        }

        using (result)
        {
            var outcome = result.Endpoint
                ?? throw new InvalidOperationException($"Durable endpoint {functionId} could not run: {Encoding.UTF8.GetString(result.Body.WrittenSpan)}");
            var response = context.Response;
            switch (outcome.Kind)
            {
                case EndpointOutcomeKind.Completed:
                    if (outcome.Finish is not null)
                    {
                        response.Headers[EndpointCalls.RunIdHeader] = outcome.RunId;
                    }

                    if (!capture.Body.IsEmpty)
                    {
                        response.ContentLength ??= capture.Body.Length;
                        await response.Body.WriteAsync(capture.Body, cancellationToken).ConfigureAwait(false);
                    }

                    if (outcome.Finish is { } finish)
                    {
                        // The client has everything before the run is recorded.
                        await response.CompleteAsync().ConfigureAwait(false);
                        await finish(_stopping).ConfigureAwait(false);
                    }

                    break;

                case EndpointOutcomeKind.HandedOver:
                    Reset(response, earlierHeaders);
                    response.Headers[EndpointCalls.RunIdHeader] = outcome.RunId;
                    await WriteHandoverAsync(response, options, new DurableEndpointRun(outcome.RunId, outcome.Token!), cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    Reset(response, earlierHeaders);
                    if (outcome.Finish is { } finishFailed)
                    {
                        await finishFailed(_stopping).ConfigureAwait(false);
                    }

                    // The app's own error handling answers the client, as it would without Inngest.
                    ExceptionDispatchInfo.Capture(outcome.Error!).Throw();
                    break;
            }
        }
    }

    private async Task ResumeAsync(HttpContext context, HttpServeRequest request, string functionId, CancellationToken cancellationToken)
    {
        var capture = new CapturedResponse(context);
        ServeResponse response;
        try
        {
            response = await _handler.HandleEndpointCallAsync(request, Function(functionId, context, capture, resumed: true), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            capture.Detach();
        }

        using (response)
        {
            // Inngest gets the answer of the SDK, never what the endpoint wrote.
            context.Response.Clear();
            await InngestEndpoint.WriteAsync(context, response, cancellationToken).ConfigureAwait(false);
        }
    }

    // Takes back what the endpoint did to the response, which has not started: its status, body and headers. The
    // headers that were there before it ran are kept, except a length, which belongs to the body that replaces it.
    private static void Reset(HttpResponse response, KeyValuePair<string, StringValues>[] earlierHeaders)
    {
        response.Clear();
        foreach (var (name, value) in earlierHeaders)
        {
            if (!string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                response.Headers[name] = value;
            }
        }
    }

    // The run's function: the rest of the pipeline, ending in the endpoint, with the response it wrote as the output.
    // Inngest reads the output of a resumed run as {"data": {"status", "headers", "body"}} and replays it to the client
    // waiting for the run.
    private FunctionDefinition Function(string functionId, HttpContext context, CapturedResponse capture, bool resumed)
    {
        var function = new DelegateFunction<JsonElement>(async (_, _) =>
        {
            await _next(context).ConfigureAwait(false);
            await capture.FlushAsync().ConfigureAwait(false);
            return Record(context.Response, capture.Body, resumed);
        });
        return new FunctionDefinition(new FunctionOptions { Id = functionId, Triggers = [] }, _ => function);
    }

    private static JsonElement Record(HttpResponse response, ReadOnlyMemory<byte> body, bool resumed)
    {
        var recorded = resumed || body.Length <= MaxRecordedResponseBody ? body : body[..MaxRecordedResponseBody];
        using var buffer = new PooledBufferWriter(recorded.Length + 512);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (resumed)
            {
                writer.WriteStartObject("data"u8);
            }

            writer.WriteNumber("status"u8, response.StatusCode);
            writer.WriteStartObject("headers"u8);
            foreach (var (name, values) in response.Headers)
            {
                if (!UnrecordedHeaders.Contains(name))
                {
                    writer.WriteString(name, values.ToString());
                }
            }

            writer.WriteEndObject();
            writer.WriteString("body"u8, Encoding.UTF8.GetString(recorded.Span));
            if (resumed)
            {
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private async Task WriteHandoverAsync(HttpResponse response, DurableEndpointOptions options, DurableEndpointRun run, CancellationToken cancellationToken)
    {
        if (options.AsyncResponse == DurableEndpointResponse.Token)
        {
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "application/json";
            using var buffer = new PooledBufferWriter(256);
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("run_id"u8, run.RunId);
                writer.WriteString("token"u8, run.Token);
                writer.WriteEndObject();
            }

            response.ContentLength = buffer.WrittenCount;
            await response.Body.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
            return;
        }

        // 303, unlike 302, tells every client to fetch the output with a GET, whatever the method of the request.
        var location = options.RedirectUrl?.Invoke(run)
            ?? new Uri(_settings.ApiBaseUrl, $"/v1/http/runs/{Uri.EscapeDataString(run.RunId)}/output?token={Uri.EscapeDataString(run.Token)}");
        response.StatusCode = StatusCodes.Status303SeeOther;
        response.Headers.Location = location.IsAbsoluteUri ? location.AbsoluteUri : location.OriginalString;
    }

    // What the run's event records: the origin Inngest calls back, which ServeOrigin sets behind a proxy or in a
    // container, and the parts of the request it replays.
    private EndpointRequest Describe(HttpContext context, byte[] body, string functionId)
    {
        var request = context.Request;
        var domain = _settings.ServeOrigin is { } origin
            ? origin.GetLeftPart(UriPartial.Authority)
            : $"{request.Scheme}://{request.Host.ToUriComponent()}";
        var query = request.QueryString.HasValue ? request.QueryString.Value![1..] : string.Empty;
        return new EndpointRequest(
            domain,
            request.Method,
            request.PathBase.Add(request.Path).ToUriComponent(),
            ClientIp(context),
            request.ContentType ?? string.Empty,
            query,
            body,
            functionId);
    }

    private static string ClientIp(HttpContext context)
    {
        var headers = context.Request.Headers;
        if (headers["X-Forwarded-For"].ToString() is { Length: > 0 } forwarded)
        {
            var first = forwarded.Split(',', 2)[0].Trim();
            if (first.Length > 0)
            {
                return first;
            }
        }

        return headers["X-Real-IP"].ToString() is { Length: > 0 } real ? real : context.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
    }

    // The route pattern, not the path, so that /orders/1 and /orders/2 are one function.
    private static string DefaultId(HttpContext context, Endpoint endpoint)
    {
        var route = endpoint is RouteEndpoint { RoutePattern.RawText: { Length: > 0 } pattern } ? pattern : context.Request.Path.Value ?? "/";
        return $"{context.Request.Method} {(route.StartsWith('/') ? route : "/" + route)}";
    }

    // Null when the body is larger than the run may store. The declared length is checked first, so a client cannot
    // make the app reserve more than the limit.
    private static async Task<byte[]?> ReadBodyAsync(HttpContext context, int limit, CancellationToken cancellationToken)
    {
        var request = context.Request;
        if (request.ContentLength == 0 || context.Features.Get<IHttpRequestBodyDetectionFeature>() is { CanHaveBody: false })
        {
            return [];
        }

        if (request.ContentLength > limit)
        {
            return null;
        }

        var buffer = new ArrayBufferWriter<byte>((int)Math.Max(1, Math.Min(request.ContentLength ?? 4096, limit)));
        var reader = request.BodyReader;
        while (true)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (buffer.WrittenCount + result.Buffer.Length > limit)
            {
                reader.AdvanceTo(result.Buffer.End);
                return null;
            }

            foreach (var segment in result.Buffer)
            {
                buffer.Write(segment.Span);
            }

            reader.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted)
            {
                return buffer.WrittenSpan.ToArray();
            }
        }
    }
}
