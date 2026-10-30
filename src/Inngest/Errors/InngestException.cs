// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>Raised when the SDK cannot talk to Inngest, for example when sending events or syncing fails.</summary>
public class InngestException : Exception
{
    public InngestException(string message)
        : base(message)
    {
    }

    public InngestException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public InngestException(string message, int statusCode, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    public int? StatusCode { get; }
}
