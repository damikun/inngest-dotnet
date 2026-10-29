// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

// Throw from a function or step to retry it, but not before the given delay or time.
public class RetryAfterException : Exception
{
    public RetryAfterException(string message, TimeSpan delay, Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        Delay = delay;
    }

    public RetryAfterException(string message, DateTimeOffset retryAt, Exception? innerException = null)
        : base(message, innerException)
    {
        RetryAt = retryAt;
    }

    public TimeSpan? Delay { get; }

    public DateTimeOffset? RetryAt { get; }
}
