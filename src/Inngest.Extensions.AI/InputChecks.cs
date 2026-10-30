// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Inngest.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Inngest.Extensions.AI;

internal static class InputChecks
{
    // Every call that becomes a step saves a digest of its input with the result. A step's id is claimed in the order the
    // calls reach the client, and calls that are made after something that takes a varying time, such as a request, reach it
    // in a different order on a replay. The call that claims the id then gets the result made for another call, which only the
    // digests can tell. It is a warning and not an exception, as an input with a part that changes between replays, such as
    // the time, would then fail on every one. The function's logger is silent while the run replays steps it has seen, which
    // is when this is found, so the warning goes to the logger of the package.
    public static void Check(string stepId, string? savedDigest, string? digest)
    {
        if (savedDigest is null || digest is null || savedDigest == digest)
        {
            return;
        }

        var context = Invocation.AmbientContext;
        ILogger logger = context?.Services.GetService<ILoggerFactory>()?.CreateLogger<InngestStepsChatClient>() ?? (ILogger)NullLogger.Instance;
        Log.InputDiffers(logger, stepId, context?.RunId ?? string.Empty);
    }
}
