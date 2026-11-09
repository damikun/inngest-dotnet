// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;

namespace Inngest.Middleware;

// Decrypts a value that was encrypted as a whole, outside a run: the durable endpoint output proxy reads a run's output
// with it. Inngest.Encryption registers its encryptor as one.
internal interface IValueDecryptor
{
    // Writes the value decrypted and returns true, or writes nothing and returns false for a value that is not encrypted.
    bool TryDecrypt(JsonElement value, Utf8JsonWriter writer);
}
