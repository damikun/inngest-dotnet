// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Inngest;
using Inngest.Client;
using Inngest.Configuration;
using Inngest.DependencyInjection;
using Inngest.Execution;
using Inngest.Realtime;
using Inngest.Registration;
using Inngest.Rest;
using Inngest.Serving;
using Inngest.Signing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

public static class InngestServiceCollectionExtensions
{
    internal const string HttpClientName = "Inngest";

    public static IInngestBuilder AddInngest(this IServiceCollection services, Action<InngestOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.AddOptions<InngestOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        // Fails the host at startup rather than on the first request or send.
        options
            .Validate(
                static o => !string.IsNullOrWhiteSpace(o.AppId) || (o.Configuration is { } section && InngestConfiguration.ReadAppId(section) is not null),
                "InngestOptions.AppId is required, for example AddInngest(options => options.AppId = \"my-app\"), or as AppId in the configuration section passed to AddInngest.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(static provider =>
        {
            var configuration = provider.GetService<IConfiguration>();
            return InngestSettings.Create(
                provider.GetRequiredService<IOptions<InngestOptions>>().Value,
                configuration is null ? Environment.GetEnvironmentVariable : name => configuration[name]);
        });

        // The default HttpClient logging writes each request's URL at Information level, and the URL of an event send
        // holds the event key. The SDK logs what goes wrong itself.
        services.AddHttpClient(HttpClientName).RemoveAllLoggers();
        services.TryAddSingleton<RequestSigner>();
        services.TryAddSingleton<IInngestApi, InngestApiClient>();
        services.TryAddSingleton<ISignalApi, SignalApi>();
        services.TryAddSingleton<ICheckpointApi, CheckpointApiClient>();
        services.TryAddSingleton<InngestClient>();
        services.TryAddSingleton<IInngestClient>(static provider => provider.GetRequiredService<InngestClient>());
        services.TryAddSingleton<IInngestRunsClient, InngestRunsClient>();
        services.TryAddSingleton<IRealtimeApi, RealtimeApi>();
        services.TryAddSingleton<IInngestRealtime, InngestRealtime>();
        services.TryAddSingleton<IFunctionRegistry, FunctionRegistry>();
        services.TryAddSingleton<IExecutionEngine, ExecutionEngine>();
        services.TryAddSingleton<InngestCommHandler>();

        return new InngestBuilder(services);
    }

    /// <summary>
    /// Takes the settings from a section such as builder.Configuration.GetSection("Inngest"). It is the lowest layer,
    /// below options set in code and the INNGEST_* variables, so the order of the calls that configure the options
    /// never matters.
    /// </summary>
    public static IInngestBuilder AddInngest(this IServiceCollection services, IConfiguration configuration, Action<InngestOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<InngestOptions>().Configure(options => options.Configuration = configuration);
        return services.AddInngest(configure);
    }
}
