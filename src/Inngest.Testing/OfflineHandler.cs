// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest.Testing;

// Stands in for the network under the SDK's own HttpClient, so that nothing a test runs reaches an Inngest server,
// not even a dev server running on the same machine.
internal sealed class OfflineHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromException<HttpResponseMessage>(new InvalidOperationException(
            $"The test host keeps runs in memory and does not send {request.Method} {request.RequestUri} to Inngest."));
}
