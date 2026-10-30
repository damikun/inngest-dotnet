// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// Encrypts and decrypts values for the encryption middleware in place of its default strategy, for example to keep
/// the keys in a vault. Values are handed over as UTF-8 JSON. An encrypted value travels in an envelope that names
/// the service by <see cref="Identifier"/>, so a service in another SDK or language can only read it with the same
/// identifier and algorithm. One instance serves every call, including calls that run at the same time, so it must be
/// thread-safe. Both methods run synchronously on the call's path, so they should not wait on I/O.
/// </summary>
public abstract class EncryptionService
{
    /// <summary>
    /// Written to the __STRATEGY__ field of every envelope this service makes, and what picks this service to decrypt
    /// an envelope that carries it. The default strategy is "inngest/libsodium".
    /// </summary>
    public abstract string Identifier { get; }

    /// <summary>Encrypts the UTF-8 JSON of a value and returns the text that goes into the data field of the envelope.</summary>
    public abstract string Encrypt(ReadOnlySpan<byte> json);

    /// <summary>
    /// Decrypts the data field of an envelope back to the UTF-8 JSON of the value. Throws when it cannot, which fails
    /// the step or run that read the value, and Inngest retries it.
    /// </summary>
    public abstract byte[] Decrypt(string data);
}
