// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Buffers;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Inngest.Encryption;

// Encrypts and decrypts the JSON values of an app for its middleware. Every call shares it, so it keeps nothing for a call.
internal sealed class PayloadEncryptor
{
    // The text that is encrypted, with characters written as they are rather than as \uXXXX escapes, as JSON.stringify
    // does for the TypeScript package.
    private static readonly JsonWriterOptions PlainText = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, SkipValidation = true };

    private readonly EncryptionService _encrypting;
    private readonly EncryptionService? _custom;
    private readonly LibSodiumEncryptionService? _libSodium;
    private readonly AesDecryptor? _aes;
    private readonly bool _decryptOnly;
    private readonly string _eventField;
    private readonly JsonSerializerOptions _serializer;

    // The serializer options are the app's, which turn the data of the events that are sent into JSON.
    public PayloadEncryptor(EncryptionOptions options, JsonSerializerOptions serializer)
    {
        var keys = EncryptionKeys.Normalize(options.Key, options.FallbackDecryptionKeys);
        if (keys.Length > 0)
        {
            _libSodium = new LibSodiumEncryptionService(keys);
            _aes = new AesDecryptor(keys);
        }

        _custom = options.Service;
        _encrypting = _custom ?? _libSodium ?? throw new InvalidOperationException("EncryptionOptions.Key is required unless EncryptionOptions.Service is set.");
        _decryptOnly = options.DecryptOnly;
        _eventField = options.EventEncryptionField;
        _serializer = serializer;
    }

    // Decrypts the fields of an event's data that hold an encrypted value, whatever they are called, as the TypeScript
    // package does. An event without such a field is kept as it is.
    public bool DecryptEvent(JsonElement @event, Utf8JsonWriter writer)
    {
        if (@event.ValueKind != JsonValueKind.Object ||
            !@event.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !HoldsEncryptedField(data))
        {
            return false;
        }

        writer.WriteStartObject();
        foreach (var property in @event.EnumerateObject())
        {
            if (property.NameEquals("data"))
            {
                writer.WritePropertyName("data");
                WriteWithDecryptedFields(data, writer);
            }
            else
            {
                property.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
        return true;
    }

    // Decrypts a value that is an encrypted value as a whole, such as a step result, and keeps any other.
    public bool DecryptValue(JsonElement value, Utf8JsonWriter writer)
    {
        if (!Envelope.TryRead(value, out var strategy, out var data))
        {
            return false;
        }

        writer.WriteRawValue(Open(strategy, data.GetString()!));
        return true;
    }

    // Encrypts a step's output or the function's output as a whole. Null has nothing to hide, and the TypeScript
    // package leaves it alone too.
    public bool EncryptOutput(JsonElement output, Utf8JsonWriter writer)
    {
        if (_decryptOnly || output.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        Seal(output, writer);
        return true;
    }

    // Encrypts the field of each event's data that EventEncryptionField names. Events without it are the same instances.
    public IReadOnlyList<InngestEvent> EncryptEvents(IReadOnlyList<InngestEvent> events)
    {
        if (_decryptOnly)
        {
            return events;
        }

        InngestEvent[]? encrypted = null;
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i] is { Data: not null } @event && EncryptEvent(@event) is { } replacement)
            {
                encrypted ??= [.. events];
                encrypted[i] = replacement;
            }
        }

        return encrypted ?? events;
    }

    // Null when the event has no such field. The data is serialized as the client would, and what is sent in its place
    // is that JSON with the field encrypted.
    private InngestEvent? EncryptEvent(InngestEvent @event)
    {
        var data = @event.Data!;
        using var document = JsonSerializer.SerializeToDocument(data, _serializer.GetTypeInfo(data.GetType()));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(_eventField, out var field))
        {
            return null;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = _serializer.Encoder }))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
            {
                if (property.NameEquals(_eventField))
                {
                    writer.WritePropertyName(_eventField);
                    Seal(field, writer);
                }
                else
                {
                    property.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        using var encrypted = JsonDocument.Parse(buffer.WrittenMemory);
        return @event with { Data = encrypted.RootElement.Clone() };
    }

    private void Seal(JsonElement value, Utf8JsonWriter writer)
    {
        var json = new ArrayBufferWriter<byte>();
        using (var plain = new Utf8JsonWriter(json, PlainText))
        {
            value.WriteTo(plain);
        }

        Envelope.Write(writer, _encrypting.Identifier, _encrypting.Encrypt(json.WrittenSpan));
    }

    private void WriteWithDecryptedFields(JsonElement data, Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        foreach (var field in data.EnumerateObject())
        {
            if (Envelope.TryRead(field.Value, out var strategy, out var encrypted))
            {
                writer.WritePropertyName(field.Name);
                writer.WriteRawValue(Open(strategy, encrypted.GetString()!));
            }
            else
            {
                field.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
    }

    private static bool HoldsEncryptedField(JsonElement data)
    {
        foreach (var field in data.EnumerateObject())
        {
            if (Envelope.TryRead(field.Value, out _, out _))
            {
                return true;
            }
        }

        return false;
    }

    // The service for the strategy the envelope names: the custom one by its identifier, or one of the built-in
    // strategies that the keys serve. Envelopes without a strategy are the AES ones of the first versions.
    private byte[] Open(string? strategy, string data)
    {
        if (strategy is not null && _custom is not null && strategy == _custom.Identifier)
        {
            return _custom.Decrypt(data);
        }

        return strategy switch
        {
            null or AesDecryptor.StrategyName => (_aes ?? throw MissingKey(strategy)).Decrypt(data),
            LibSodiumEncryptionService.StrategyName => (_libSodium ?? throw MissingKey(strategy)).Decrypt(data),
            _ => throw new CryptographicException($"No encryption service handles the strategy \"{strategy}\"."),
        };
    }

    private static CryptographicException MissingKey(string? strategy) =>
        new($"The value is encrypted with {(strategy is null ? "the legacy AES format" : $"the strategy \"{strategy}\"")}, which needs EncryptionOptions.Key to decrypt.");
}
