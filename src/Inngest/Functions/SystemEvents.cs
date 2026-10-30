// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// The events Inngest itself sends about function runs. A function triggered by one of them, filtered on
/// event.data.function_id, can react to how the runs of another function end.
/// </summary>
public static class SystemEvents
{
    /// <summary>A run failed after its last attempt. The data is FunctionFailure.</summary>
    public const string FunctionFailed = "inngest/function.failed";

    /// <summary>A run ended: it completed, failed or was cancelled. The data is FunctionFinished.</summary>
    public const string FunctionFinished = "inngest/function.finished";

    /// <summary>A run was cancelled. The data is FunctionCancelled.</summary>
    public const string FunctionCancelled = "inngest/function.cancelled";

    /// <summary>What an invoked function receives as its event.</summary>
    public const string FunctionInvoked = "inngest/function.invoked";

    /// <summary>What a function receives as its event when its cron schedule fires.</summary>
    public const string ScheduledTimer = "inngest/scheduled.timer";
}
