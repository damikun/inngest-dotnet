// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Encodings.Web;
using System.Text.Json;
using Inngest.Client;
using Inngest.Configuration;
using Inngest.Execution;
using Inngest.Internal;
using Inngest.Registration;
using Inngest.Signing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Inngest.Serving;

// Implements the serve endpoint protocol: GET introspection, PUT sync and POST invocation. Status codes, bodies
// and headers follow the TypeScript SDK; docs/design.md describes the protocol.
internal sealed partial class InngestCommHandler(
    InngestSettings settings,
    IFunctionRegistry registry,
    IExecutionEngine engine,
    IInngestApi api,
    RequestSigner signer,
    IServiceScopeFactory scopeFactory,
    ILogger<InngestCommHandler> logger)
{
    private const string ContentTypeHeader = "Content-Type";
    private const string JsonContentType = "application/json";
    private const string FunctionIdParameter = "fnId";
    private const string StepIdParameter = "stepId";
    private const string ProbeParameter = "probe";
    private const string TrustProbe = "trust";
    private const string InBand = "in_band";
    private const string OutOfBand = "out_of_band";
    private const int RegistrationCapacity = 4096;

    // Bodies only go to Inngest, never into HTML, so they keep quotes and non-ASCII text readable.
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly byte[] UnauthorizedBody = """{"message":"Unauthorized"}"""u8.ToArray();
    private static readonly byte[] MethodNotAllowedBody = """{"message":"Method not allowed"}"""u8.ToArray();
    private static readonly byte[] MisconfiguredBody = """{"code":"internal_server_error"}"""u8.ToArray();

    // Dev mode trusts every caller and signs nothing.
    private static readonly SignatureCheck DevCaller = new(true, null, null);
    private static readonly SignatureCheck NoSigningKey = SignatureCheck.Failed("No signing key is configured.");

    private readonly Introspection _introspection = new(settings, registry);

    private static Reply Unauthorized => new(401, UnauthorizedBody);

    public async Task<ServeResponse> HandleAsync(IServeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        SignatureCheck caller;
        if (settings.IsDev)
        {
            caller = DevCaller;
        }
        else if (settings.SigningKey is { } signingKey)
        {
            caller = Verify(request, signingKey);
        }
        else
        {
            Log.MissingSigningKey(logger);
            return Respond(request, NoSigningKey, new Reply(500, MisconfiguredBody));
        }

        var method = request.Method;
        Reply reply;
        try
        {
            if (IsMethod(method, "POST"))
            {
                reply = await CallAsync(request, caller, cancellationToken).ConfigureAwait(false);
            }
            else if (IsMethod(method, "GET"))
            {
                reply = Introspect(request, caller);
            }
            else if (IsMethod(method, "PUT"))
            {
                reply = await SyncAsync(request, caller, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Log.MethodNotAllowed(logger, method);
                reply = new Reply(405, MethodNotAllowedBody);
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            Log.RequestFailed(logger, method, exception);
            reply = InternalError(exception);
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

    // Every request is checked in cloud mode, not only those that require a signature, so that responses to
    // Inngest can be signed and anonymous callers can be told nothing. As in the TypeScript SDK, only POST and
    // PUT bodies are covered.
    private SignatureCheck Verify(IServeRequest request, SigningKey signingKey)
    {
        ReadOnlySpan<byte> body = IsMethod(request.Method, "POST") || IsMethod(request.Method, "PUT") ? request.Body.Span : [];
        return signer.Verify(request.GetHeader(Headers.Signature), body, signingKey, settings.SigningKeyFallback);
    }

    private void LogRejected(IServeRequest request, SignatureCheck caller)
    {
        if (string.IsNullOrEmpty(request.GetHeader(Headers.Signature)))
        {
            Log.Unsigned(logger, request.Method);
        }
        else
        {
            Log.Unauthorized(logger, request.Method, caller.Error);
        }
    }

    private ServeResponse Respond(IServeRequest request, SignatureCheck caller, Reply reply)
    {
        var headers = new List<KeyValuePair<string, string>>(capacity: 12)
        {
            new(ContentTypeHeader, JsonContentType),
            new(Headers.SdkHandled, "true"),
        };

        // As in the TypeScript SDK, a caller that failed authentication learns nothing about the SDK: every
        // X-Inngest-* header except X-Inngest-SDK-Handled is left out.
        if (caller.IsValid)
        {
            headers.Add(new(Headers.Sdk, SdkInfo.Header));
            headers.Add(new(Headers.RequestVersion, Headers.ExecutionVersion));
            headers.Add(new(Headers.Framework, request.Framework));
            if (!string.IsNullOrEmpty(settings.Environment))
            {
                headers.Add(new(Headers.Environment, settings.Environment));
            }

            if (reply.SyncKind is not null)
            {
                headers.Add(new(Headers.SyncKind, reply.SyncKind));
            }

            if (reply.NoRetry is { } noRetry)
            {
                headers.Add(new(Headers.NoRetry, noRetry ? "true" : "false"));
            }

            if (caller.KeyUsed is not null)
            {
                headers.Add(new(Headers.Signature, signer.Sign(reply.Body.Span, caller.KeyUsed)));
            }
        }

        if (reply.RetryAfter is not null)
        {
            headers.Add(new(Headers.RetryAfter, reply.RetryAfter));
        }

        Forward(request, Headers.TraceParent, headers);
        Forward(request, Headers.TraceState, headers);
        return new ServeResponse(reply.StatusCode, headers, reply.Body, reply.Owner);
    }

    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static void Forward(IServeRequest request, string name, List<KeyValuePair<string, string>> headers)
    {
        if (request.GetHeader(name) is { Length: > 0 } value)
        {
            headers.Add(new(name, value));
        }
    }

    private static Reply InvalidRequest(string message)
    {
        var body = new PooledBufferWriter();
        using (var writer = new Utf8JsonWriter(body, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("code"u8, "invalid_request");
            writer.WriteString("message"u8, message);
            writer.WriteEndObject();
        }

        return new Reply(400, body.WrittenMemory, body);
    }

    // A failure of the SDK rather than of the function. Inngest shows name, message and stack and retries the call.
    private static Reply InternalError(Exception exception) =>
        Error(500, "internal", exception.Message, exception.GetType().Name, exception.ToString());

    private static Reply Error(int statusCode, string? type, string message, string name = "Error", string stack = "")
    {
        var body = new PooledBufferWriter();
        using (var writer = new Utf8JsonWriter(body, WriterOptions))
        {
            writer.WriteStartObject();
            if (type is not null)
            {
                writer.WriteString("type"u8, type);
            }

            writer.WriteString("name"u8, name);
            writer.WriteString("message"u8, message);
            writer.WriteString("stack"u8, stack);
            writer.WriteEndObject();
        }

        return new Reply(statusCode, body.WrittenMemory, body);
    }

    private static bool IsMethod(string method, string expected) =>
        string.Equals(method, expected, StringComparison.OrdinalIgnoreCase);

    // What one of the method handlers decided, before the common headers and the signature are added. Owner, when
    // set, holds the memory Body points into.
    private readonly record struct Reply(int StatusCode, ReadOnlyMemory<byte> Body, IDisposable? Owner = null)
    {
        public string? SyncKind { get; init; }

        public bool? NoRetry { get; init; }

        public string? RetryAfter { get; init; }
    }
}
