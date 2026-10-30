// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;

namespace Inngest.Encryption;

// How an encrypted value travels, as @inngest/middleware-encryption writes it:
//   {"__ENCRYPTED__":true,"__STRATEGY__":"inngest/libsodium","data":"<base64>"}
// Its first versions wrote no __STRATEGY__ and encrypted with AES; those are read and never written.
internal static class Envelope
{
    private static readonly JsonEncodedText EncryptedProperty = JsonEncodedText.Encode("__ENCRYPTED__");
    private static readonly JsonEncodedText StrategyProperty = JsonEncodedText.Encode("__STRATEGY__");
    private static readonly JsonEncodedText DataProperty = JsonEncodedText.Encode("data");

    // The same test as the TypeScript package's isEncryptedValue and isV0EncryptedValue. The strategy is null for the
    // envelopes without one. Anything else, such as a strategy that is not a string, is an ordinary value.
    public static bool TryRead(JsonElement value, out string? strategy, out JsonElement data)
    {
        strategy = null;
        data = default;
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("__ENCRYPTED__", out var marker) || marker.ValueKind != JsonValueKind.True ||
            !value.TryGetProperty("data", out data) || data.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        if (!value.TryGetProperty("__STRATEGY__", out var name))
        {
            return true;
        }

        if (name.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        strategy = name.GetString();
        return true;
    }

    public static void Write(Utf8JsonWriter writer, string strategy, string data)
    {
        writer.WriteStartObject();
        writer.WriteBoolean(EncryptedProperty, true);
        writer.WriteString(StrategyProperty, strategy);
        writer.WriteString(DataProperty, data);
        writer.WriteEndObject();
    }
}
