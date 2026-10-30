// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Microsoft.Extensions.Logging;

namespace Inngest.AzureFunctions;

internal static partial class Log
{
    [LoggerMessage(EventId = 370, Level = LogLevel.Warning,
        Message = "Inngest streaming is on, but this function app uses the built-in HTTP model of the Functions worker, which sends the response to the Functions host when the function returns, so nothing keeps a long call open. Streaming needs ConfigureFunctionsWebApplication().")]
    public static partial void StreamingNeedsAspNetCoreIntegration(ILogger logger);
}
