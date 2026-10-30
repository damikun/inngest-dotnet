// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Microsoft.Extensions.Logging;

namespace Inngest.AwsLambda;

internal static partial class Log
{
    [LoggerMessage(EventId = 380, Level = LogLevel.Warning, Message = "InngestOptions.Streaming is on, but a Lambda function answers with one buffered response, so streaming has no effect.")]
    public static partial void StreamingHasNoEffect(ILogger logger);
}
