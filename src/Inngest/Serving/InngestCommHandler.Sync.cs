// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Inngest.Client;
using Inngest.Internal;
using Inngest.Registration;
using Inngest.Signing;

namespace Inngest.Serving;

// Introspection and the in-band and out-of-band syncs.
internal sealed partial class InngestCommHandler
{
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
}
