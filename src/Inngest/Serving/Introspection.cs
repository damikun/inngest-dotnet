// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Security.Cryptography;
using System.Text.Json;
using Inngest.Internal;
using Inngest.Registration;
using Inngest.Signing;

namespace Inngest.Serving;

// The introspection body of schema 2024-05-24: the answer to GET and part of an in-band sync. Only a caller whose
// signature was verified gets the authenticated form, with the app id, origins and key hashes.
internal sealed class Introspection(InngestSettings settings, IFunctionRegistry registry)
{
    private const string SchemaVersion = "2024-05-24";

    private readonly string? _eventKeyHash = string.IsNullOrEmpty(settings.EventKey) ? null : SigningKey.HashEventKey(settings.EventKey);
    private readonly string? _signingKeyHash = ShortHash(settings.SigningKey);
    private readonly string? _signingKeyFallbackHash = ShortHash(settings.SigningKeyFallback);

    public void Write(Utf8JsonWriter writer, string framework, bool authenticated)
    {
        writer.WriteStartObject();
        if (authenticated)
        {
            writer.WriteString("api_origin"u8, settings.ApiBaseUrl.AbsoluteUri);
            writer.WriteString("app_id"u8, settings.AppId);
            writer.WriteBoolean("authentication_succeeded"u8, true);
            WriteCapabilities(writer);
            writer.WriteString("env"u8, settings.Environment);
            writer.WriteString("event_api_origin"u8, settings.EventApiBaseUrl.AbsoluteUri);
            writer.WriteString("event_key_hash"u8, _eventKeyHash);
            writer.WriteStartObject("extra"u8);
            writer.WriteBoolean("is_streaming"u8, false);
            writer.WriteEndObject();
            writer.WriteString("framework"u8, framework);
            WriteSummary(writer);
            writer.WriteString("sdk_language"u8, SdkInfo.Language);
            writer.WriteString("sdk_version"u8, SdkInfo.Version);
            writer.WriteString("serve_origin"u8, settings.ServeOrigin?.OriginalString);
            writer.WriteString("serve_path"u8, settings.ServePath);
            writer.WriteString("signing_key_fallback_hash"u8, _signingKeyFallbackHash);
            writer.WriteString("signing_key_hash"u8, _signingKeyHash);
        }
        else
        {
            // Null rather than false: nothing was checked, which only happens in dev mode.
            writer.WriteNull("authentication_succeeded"u8);
            WriteCapabilities(writer);
            WriteSummary(writer);
        }

        writer.WriteEndObject();
    }

    private static void WriteCapabilities(Utf8JsonWriter writer)
    {
        writer.WriteStartObject("capabilities"u8);
        writer.WriteString("trust_probe"u8, "v1");
        writer.WriteEndObject();
    }

    private void WriteSummary(Utf8JsonWriter writer)
    {
        writer.WriteNumber("function_count"u8, registry.Functions.Count);
        writer.WriteBoolean("has_event_key"u8, !string.IsNullOrEmpty(settings.EventKey));
        writer.WriteBoolean("has_signing_key"u8, settings.SigningKey is not null);
        writer.WriteBoolean("has_signing_key_fallback"u8, settings.SigningKeyFallback is not null);
        writer.WriteString("mode"u8, settings.IsDev ? "dev" : "cloud");
        writer.WriteString("schema_version"u8, SchemaVersion);
    }

    // The first 12 hex digits of the hashed key tell keys apart without giving the hash away. The bearer token
    // ends with the full hash.
    private static string? ShortHash(SigningKey? key) =>
        key?.BearerToken.Substring(key.BearerToken.Length - (SHA256.HashSizeInBytes * 2), 12);
}
