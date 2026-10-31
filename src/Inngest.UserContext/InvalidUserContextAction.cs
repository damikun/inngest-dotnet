// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

namespace Inngest;

/// <summary>What a run does when its event carries a user that cannot be used.</summary>
public enum InvalidUserContextAction
{
    /// <summary>
    /// Writes a warning without the payload and runs without a user: <see cref="ICurrentUser.IsAuthenticated"/> is false.
    /// </summary>
    Anonymous,

    /// <summary>
    /// Fails the run. A user that has expired, was made for another event or has an unknown format fails without retries.
    /// One that cannot be unprotected is retried, because the app may not have the sender's newest key yet; if it never
    /// can be read, the last attempt fails. A failure handler never fails on its user: it takes the Anonymous path.
    /// </summary>
    FailRun,
}
