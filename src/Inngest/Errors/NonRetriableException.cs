// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

// Throw from a function or step to fail it immediately instead of retrying.
public class NonRetriableException : Exception
{
    public NonRetriableException()
    {
    }

    public NonRetriableException(string message)
        : base(message)
    {
    }

    public NonRetriableException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
