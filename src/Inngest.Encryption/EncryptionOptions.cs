// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// Settings of the encryption middleware. Services that exchange encrypted data, in .NET or in the TypeScript SDK with
/// @inngest/middleware-encryption, need the same key, and the same EventEncryptionField for the events they send to
/// each other.
/// </summary>
public sealed class EncryptionOptions
{
    /// <summary>
    /// The key that encrypts, and the first one tried when decrypting. It can be any text; the key used is its BLAKE2b
    /// hash, so its length does not matter and white space around it is ignored. Use a long random value from a secret
    /// store, not a password. Required unless <see cref="Service"/> is set.
    /// </summary>
    public string? Key { get; set; }

    /// <summary>
    /// Keys that only decrypt, tried after <see cref="Key"/>, so that values written with a key being replaced can still
    /// be read. Blank entries are ignored.
    /// </summary>
    public IReadOnlyList<string> FallbackDecryptionKeys { get; set; } = [];

    /// <summary>
    /// Decrypts but encrypts nothing. Use it to deploy the middleware to every service before encryption is switched on
    /// in any, and to phase encryption out again until no run holds encrypted data.
    /// </summary>
    public bool DecryptOnly { get; set; }

    /// <summary>
    /// The top-level field of an event's data that is encrypted when the event is sent, matched against the JSON name
    /// the serializer writes. When events arrive, every top-level field of their data that holds an encrypted value is
    /// decrypted, whatever its name.
    /// </summary>
    public string EventEncryptionField { get; set; } = "encrypted";

    /// <summary>
    /// Encrypts with this service instead of the default libsodium strategy. Values that the default strategies
    /// wrote are still decrypted when <see cref="Key"/> is set.
    /// </summary>
    public EncryptionService? Service { get; set; }
}
