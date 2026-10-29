// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Inngest.Client;
using Inngest.Execution;
using Inngest.Functions;
using Inngest.Internal;
using Inngest.Registration;
using Inngest.Signing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Inngest.Serving;

// Implements the serve endpoint protocol: GET introspection, PUT sync and POST invocation. Status codes, bodies
// and headers follow the TypeScript SDK; docs/design.md describes the protocol.
internal sealed class InngestCommHandler(
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

    private Reply Introspect(IServeRequest request, SignatureCheck caller)
    {
        // Cloud mode answers only verified requests: even the unauthenticated body would tell anyone the mode,
        // the function count and which keys are set.
        if (!caller.IsValid)
        {
            LogRejected(request, caller);
            return Unauthorized;
        }

        var body = new PooledBufferWriter(1024);
        using (var writer = new Utf8JsonWriter(body, WriterOptions))
        {
            _introspection.Write(writer, request.Framework, authenticated: caller.KeyUsed is not null);
        }

        return new Reply(200, body.WrittenMemory, body);
    }

    private async Task<Reply> SyncAsync(IServeRequest request, SignatureCheck caller, CancellationToken cancellationToken)
    {
        if (settings.AllowInBandSync && request.GetHeader(Headers.SyncKind) == InBand)
        {
            return SyncInBand(request, caller);
        }

        if (!caller.IsValid && !settings.AllowUnsignedSync)
        {
            LogRejected(request, caller);
            return Unauthorized;
        }

        return await SyncOutOfBandAsync(request, caller, cancellationToken).ConfigureAwait(false);
    }

    // The app posts its configuration to Inngest and reports how that went. Unsigned requests are accepted by
    // default, as in the TypeScript SDK, because the app only ever calls the Inngest it is configured for; they are
    // told only whether it worked.
    private async Task<Reply> SyncOutOfBandAsync(IServeRequest request, SignatureCheck caller, CancellationToken cancellationToken)
    {
        var deployId = request.GetQuery(ServeUrl.DeployIdParameter);
        if (deployId is "" or "undefined")
        {
            // Some callers send the literal "undefined" when they have no deploy id.
            deployId = null;
        }

        Uri serveUrl;
        RegisterResult result;
        using (var registration = new PooledBufferWriter(RegistrationCapacity))
        {
            try
            {
                serveUrl = ServeUrl.Resolve(request.Url, settings.ServeOrigin, settings.ServePath);
                registry.WriteRegistration(registration, serveUrl, request.Framework);
                result = await api
                    .RegisterAsync(registration.WrittenMemory, deployId, request.GetHeader(Headers.ServerKind), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                Log.SyncFailed(logger, exception);
                return SyncResult(500, caller.IsValid ? $"Failed to register; {exception.Message}" : "Failed to register", modified: false);
            }
        }

        if (!result.Success)
        {
            Log.SyncRejected(logger, result.StatusCode, result.Error);
            return SyncResult(500, string.IsNullOrEmpty(result.Error) || !caller.IsValid ? "Failed to register" : result.Error, modified: false);
        }

        if (result.Modified)
        {
            Log.Synced(logger, registry.Functions.Count, serveUrl);
        }
        else
        {
            Log.SyncUnchanged(logger, serveUrl);
        }

        return SyncResult(200, "Successfully registered", result.Modified);
    }

    // Inngest takes the configuration from the response, so the app never has to call Inngest. The URL comes from
    // the signed body: Inngest knows the address it called, which the app may not.
    private Reply SyncInBand(IServeRequest request, SignatureCheck caller)
    {
        if (!caller.IsValid)
        {
            LogRejected(request, caller);
            return Unauthorized;
        }

        if (!TryReadInBandUrl(request.Body, out var url, out var problem))
        {
            Log.InvalidInBandSync(logger, problem);
            return InvalidRequest(problem);
        }

        var serveUrl = ServeUrl.Resolve(url, settings.ServeOrigin, settings.ServePath);
        using var registration = new PooledBufferWriter(RegistrationCapacity);
        registry.WriteRegistration(registration, serveUrl, request.Framework);
        using var configuration = JsonDocument.Parse(registration.WrittenMemory);
        var source = configuration.RootElement;
        var authenticated = caller.KeyUsed is not null;

        var body = new PooledBufferWriter(registration.WrittenCount + 1024);
        using (var writer = new Utf8JsonWriter(body, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("app_id"u8, settings.AppId);
            if (settings.AppVersion is not null)
            {
                writer.WriteString("appVersion"u8, settings.AppVersion);
            }

            CopyProperty(writer, source, "capabilities"u8);
            writer.WriteString("env"u8, settings.Environment);
            CopyProperty(writer, source, "framework"u8);
            CopyProperty(writer, source, "functions"u8);
            CopyProperty(writer, source, "featureObservations"u8);
            writer.WritePropertyName("inspection"u8);
            _introspection.Write(writer, request.Framework, authenticated);
            writer.WriteString("sdk_author"u8, "inngest-dotnet");
            writer.WriteString("sdk_language"u8, authenticated ? SdkInfo.Language : "");
            writer.WriteString("sdk_version"u8, authenticated ? SdkInfo.Version : "");
            CopyProperty(writer, source, "sdk"u8);
            CopyProperty(writer, source, "url"u8);
            writer.WriteEndObject();
        }

        Log.SyncedInBand(logger, serveUrl);
        return new Reply(200, body.WrittenMemory, body) { SyncKind = InBand };
    }

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

    // The in-band body is exactly {"url": "<absolute URL>"}; anything else is rejected, as by the TypeScript SDK's
    // strict schema.
    private static bool TryReadInBandUrl(ReadOnlyMemory<byte> body, [NotNullWhen(true)] out Uri? url, [NotNullWhen(false)] out string? problem)
    {
        url = null;
        string? value = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                problem = "The body must be a JSON object with a url.";
                return false;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.NameEquals("url"u8))
                {
                    problem = $"Unrecognized key \"{property.Name}\".";
                    return false;
                }

                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    problem = "url must be a string.";
                    return false;
                }

                value = property.Value.GetString();
            }
        }
        catch (JsonException)
        {
            problem = "The body must be a JSON object with a url.";
            return false;
        }

        if (value is null)
        {
            problem = "url is required.";
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            problem = "url must be an absolute http or https URL.";
            return false;
        }

        url = parsed;
        problem = null;
        return true;
    }

    private static void CopyProperty(Utf8JsonWriter writer, JsonElement source, ReadOnlySpan<byte> name)
    {
        if (source.TryGetProperty(name, out var value))
        {
            writer.WritePropertyName(name);
            value.WriteTo(writer);
        }
    }

    private static Reply SyncResult(int statusCode, string message, bool modified)
    {
        var body = new PooledBufferWriter();
        using (var writer = new Utf8JsonWriter(body, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("message"u8, message);
            writer.WriteBoolean("modified"u8, modified);
            writer.WriteEndObject();
        }

        return new Reply(statusCode, body.WrittenMemory, body) { SyncKind = OutOfBand };
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
