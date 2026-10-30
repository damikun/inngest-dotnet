// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

namespace Inngest.Tool;

// A failure the user can act on. The tool prints the message, without a stack trace, and exits with 1.
internal sealed class ToolException(string message, Exception? innerException = null) : Exception(message, innerException)
{
    // The Inngest CLI could not be obtained here, which running the dev server in Docker works around.
    public bool SuggestsDocker { get; init; }
}
