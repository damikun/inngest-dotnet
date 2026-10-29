// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text;

namespace Inngest;

// An error Inngest recorded, such as the cause of a failed step, rebuilt as an exception. It reports the recorded
// type name, message and stack trace, which may come from another process or language, and its inner exception is
// the recorded cause.
public sealed class FunctionErrorException : Exception
{
    public FunctionErrorException(FunctionError error)
        : base(error?.Message, error?.Cause is { } cause ? new FunctionErrorException(cause) : null)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }

    public FunctionError Error { get; }

    public override string? StackTrace => Error.Stack;

    // Formatted like Exception.ToString, with the recorded type name in place of this type's.
    public override string ToString()
    {
        var text = new StringBuilder(Error.Name);
        if (Message.Length > 0)
        {
            text.Append(": ").Append(Message);
        }

        if (InnerException is { } inner)
        {
            text.Append(" ---> ").Append(inner).AppendLine().Append("   --- End of inner exception stack trace ---");
        }

        if (!string.IsNullOrEmpty(Error.Stack))
        {
            text.AppendLine().Append(Error.Stack);
        }

        return text.ToString();
    }
}
