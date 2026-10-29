// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace Inngest.Execution;

internal static class StepIds
{
    private const int StackBufferSize = 256;

    // Inngest stores a step under the lowercase hex SHA-1 of the id the code passed, with ":<n>" appended for
    // repeated ids.
    [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "SHA-1 is how the Inngest protocol identifies steps; it is not used for security.")]
    public static string Hash(string id)
    {
        var maxLength = Encoding.UTF8.GetMaxByteCount(id.Length);
        byte[]? rented = null;
        var buffer = maxLength <= StackBufferSize
            ? stackalloc byte[StackBufferSize]
            : rented = ArrayPool<byte>.Shared.Rent(maxLength);
        try
        {
            var length = Encoding.UTF8.GetBytes(id, buffer);
            Span<byte> hash = stackalloc byte[SHA1.HashSizeInBytes];
            SHA1.HashData(buffer[..length], hash);
            return Convert.ToHexStringLower(hash);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }
}
