// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Inngest.Connect;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Inngest;

public static class InngestConnectBuilderExtensions
{
    /// <summary>
    /// Runs the app's functions in a hosted worker that holds an outbound WebSocket to Inngest, so the app needs no
    /// HTTP endpoint and no MapInngest. The worker connects when the host starts. When the host stops, it takes no new
    /// work and disconnects once the executions it is running have finished; executions still running when
    /// HostOptions.ShutdownTimeout runs out are cancelled.
    /// </summary>
    public static IInngestBuilder AddConnect(this IInngestBuilder builder, Action<ConnectOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var services = builder.Services;
        var options = services.AddOptions<ConnectOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.TryAddSingleton(static provider =>
        {
            var configuration = provider.GetService<IConfiguration>();
            return ConnectSettings.Create(
                provider.GetRequiredService<IOptions<ConnectOptions>>().Value,
                configuration is null ? Environment.GetEnvironmentVariable : name => configuration[name]);
        });
        services.TryAddSingleton<ConnectApi>();
        services.TryAddSingleton<InngestConnection>();
        services.TryAddSingleton<IInngestConnection>(static provider => provider.GetRequiredService<InngestConnection>());
        services.AddHostedService<ConnectWorker>();
        return builder;
    }
}
