// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Inngest;
using Inngest.Blazor;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

public static class InngestBlazorServiceCollectionExtensions
{
    /// <summary>
    /// Makes components read realtime messages from Inngest itself, through IInngestRealtime, which AddInngest
    /// registers. Use it on the server: Blazor Server, the interactive server render mode, and the server project of an
    /// app that also renders in WebAssembly. Inngest is reached with the app's signing key, so nothing of it reaches
    /// the browser. The source registered last, by this or by AddInngestRealtimeRelaySource, is the one components get.
    /// </summary>
    public static IServiceCollection AddInngestRealtimeServerSource(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The registration made last is the one components get.
        services.Replace(ServiceDescriptor.Singleton<IRealtimeSource, ServerRealtimeSource>());
        return services;
    }

    /// <summary>
    /// Makes components read realtime messages from the app's MapInngestRealtime endpoint, as server-sent events over an
    /// HttpClient, so a browser needs no signing key. Use it in WebAssembly, in the client project of an app that renders
    /// there. It returns the builder of the HttpClient, for message handlers such as the one that attaches an access
    /// token. The source registered last, by this or by AddInngestRealtimeServerSource, is the one components get.
    /// </summary>
    public static IHttpClientBuilder AddInngestRealtimeRelaySource(this IServiceCollection services, Action<RealtimeRelayOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<RealtimeRelayOptions>().Configure(configure);
        services.TryAddSingleton(TimeProvider.System);

        // Scoped, for the page's NavigationManager, which is scoped on the server and a singleton in the browser.
        services.Replace(ServiceDescriptor.Scoped<IRealtimeSource, RelayRealtimeSource>());

        // The default logging of an HttpClient writes four lines to the browser's console for each connection, and the
        // source logs what goes wrong itself. An app that wants them adds a logger to the builder.
        return services.AddHttpClient(RelayRealtimeSource.HttpClientName).RemoveAllLoggers();
    }
}
