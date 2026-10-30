// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Inngest.Sentry;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest;

public static class InngestSentryBuilderExtensions
{
    /// <summary>
    /// Adds the middleware that reports the errors of functions and steps to Sentry, with the app, the function, the run
    /// and the event as tags, and a trace derived from the run id that all calls of a run share. Sentry is set up as
    /// usual, with SentrySdk.Init or UseSentry: the middleware uses the IHub the app registered, and otherwise the
    /// static hub, and does nothing while Sentry is not enabled. Add it before other middleware so that their work falls
    /// inside its scope.
    /// </summary>
    public static IInngestBuilder AddSentryReporting(this IInngestBuilder builder, Action<SentryReportingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = builder.Services.AddOptions<SentryReportingOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        return builder.AddMiddleware<SentryMiddleware>();
    }
}
