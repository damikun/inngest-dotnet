// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Inngest.Steps;

namespace Microsoft.Extensions.DependencyInjection;

public static class InngestHttpClientBuilderExtensions
{
    /// <summary>
    /// Requests the client sends from an Inngest function become fetch steps: Inngest makes them, so no worker waits,
    /// and a replay gets the memoized response instead of sending them again. Requests sent anywhere else, including
    /// inside step handlers, go out as usual. Add it last, so that it sits next to the network.
    /// </summary>
    public static IHttpClientBuilder AddInngestStepFetch(this IHttpClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddHttpMessageHandler(static () => new StepFetchHandler());
    }
}
