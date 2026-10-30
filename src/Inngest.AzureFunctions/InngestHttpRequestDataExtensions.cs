// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Inngest.AzureFunctions;

namespace Microsoft.Azure.Functions.Worker.Http;

public static class InngestHttpRequestDataExtensions
{
    /// <summary>
    /// Serves the Inngest endpoint from an HTTP-triggered function: GET answers introspection, PUT syncs the app and POST
    /// runs functions. Return the result from the function. It works with the built-in HTTP model of the isolated worker and
    /// with its ASP.NET Core integration, where a call can be streamed (<see cref="Inngest.InngestOptions.Streaming"/>); the
    /// built-in model sends the response to the Functions host only when the function returns.
    /// </summary>
    /// <remarks>
    /// Declare the function with <c>AuthorizationLevel.Anonymous</c>: Inngest signs its requests, and the SDK checks them.
    /// </remarks>
    public static Task<HttpResponseData> ServeInngestAsync(this HttpRequestData request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return FunctionsEndpoint.ServeAsync(request);
    }
}
