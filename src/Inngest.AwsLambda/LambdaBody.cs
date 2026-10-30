// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text;
using Inngest.Internal;

namespace Inngest.AwsLambda;

internal static class LambdaBody
{
    // The bytes Inngest signed: the UTF-8 of the body text, or what a base64 body decodes to (API Gateway encodes
    // bodies when the API treats their content type as binary). Null when there is no body. The caller disposes the
    // buffer after the call, as the ASP.NET Core endpoint does.
    public static PooledBufferWriter? Decode(string? body, bool isBase64Encoded)
    {
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        if (!isBase64Encoded)
        {
            var text = new PooledBufferWriter(Encoding.UTF8.GetByteCount(body));
            text.Advance(Encoding.UTF8.GetBytes(body, text.GetSpan()));
            return text;
        }

        // Three bytes for every four characters, rounded up so that padding never leaves too little room.
        var bytes = new PooledBufferWriter((body.Length + 3) / 4 * 3);
        if (!Convert.TryFromBase64String(body, bytes.GetSpan(), out var written))
        {
            bytes.Dispose();
            throw new FormatException("The body of the event is marked as base64 but is not valid base64.");
        }

        bytes.Advance(written);
        return bytes;
    }
}
