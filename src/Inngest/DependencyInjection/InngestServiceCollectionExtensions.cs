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
using Inngest.Signing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

public static class InngestServiceCollectionExtensions
{
    internal const string HttpClientName = "Inngest";

    // What AddStandardResilienceHandler, AddStandardHedgingHandler and AddResilienceHandler add.
    private const string ResilienceHandlerTypeName = "Microsoft.Extensions.Http.Resilience.ResilienceHandler";

    public static IInngestBuilder AddInngest(this IServiceCollection services, Action<InngestOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.AddOptions<InngestOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        // Fails the host at startup rather than on the first request or send: first for a missing app id, then for
        // anything else in the settings the SDK cannot read.
        options
            .Validate(
                InngestOptionsValidator.HasAppId,
                "InngestOptions.AppId is required, for example AddInngest(options => options.AppId = \"my-app\"), or as AppId in the configuration section passed to AddInngest.")
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<InngestOptions>, InngestOptionsValidator>());

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(static provider => InngestSettings.Create(
            provider.GetRequiredService<IOptions<InngestOptions>>().Value,
            InngestOptionsValidator.VariablesOf(provider)));

        // The default HttpClient logging writes each request's URL at Information level, and the URL of an event send
        // holds the event key. The SDK logs what goes wrong itself.
        //
        // The SDK retries what is worth retrying itself, sends a signal and a stream once on purpose, and holds requests
        // open for as long as Inngest does, such as the one for the output of a durable endpoint. A resilience handler
        // that the app adds to every client, as Aspire's service defaults do, would retry on top of that, send again what
        // must go once and cut those requests short, so the SDK's client goes without. It is matched by name, so the SDK
        // needs no reference to Microsoft.Extensions.Http.Resilience. One the app adds to this client after AddInngest
        // stays.
        services.AddHttpClient(HttpClientName)
            .RemoveAllLoggers()
            .ConfigureAdditionalHttpMessageHandlers(static (handlers, _) =>
            {
                for (var i = handlers.Count - 1; i >= 0; i--)
                {
                    if (handlers[i].GetType().FullName == ResilienceHandlerTypeName)
                    {
                        handlers.RemoveAt(i);
                    }
                }
            });
        services.TryAddSingleton<RequestSigner>();
        services.TryAddSingleton<IInngestApi, InngestApiClient>();
        services.TryAddSingleton<ISignalApi, SignalApi>();
        services.TryAddSingleton<CheckpointInstance>();
        services.TryAddSingleton<CheckpointApiClient>();
        services.TryAddSingleton<ICheckpointApi>(static provider => provider.GetRequiredService<CheckpointApiClient>());
        services.TryAddSingleton<IEndpointCheckpointApi>(static provider => provider.GetRequiredService<CheckpointApiClient>());
        services.TryAddSingleton<EndpointRunSaves>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, EndpointRunSaves>(static provider => provider.GetRequiredService<EndpointRunSaves>()));

        // The steps of the durable endpoint or function whose code is running, for handlers that take them as a
        // parameter or a constructor argument.
        services.TryAddTransient<IStepTools>(static _ => Invocation.AmbientSteps ?? throw new InvalidOperationException(
            "IStepTools is only available while a durable endpoint or an Inngest function runs, outside the handler of a step. " +
            "Mark the endpoint with WithDurableEndpoint() and add app.UseInngestDurableEndpoints()."));
        services.TryAddSingleton<InngestClient>();
        services.TryAddSingleton<IInngestClient>(static provider => provider.GetRequiredService<InngestClient>());
        services.TryAddSingleton<IInngestRunsClient, InngestRunsClient>();
        services.TryAddSingleton<IInngestManagementClient, InngestManagementClient>();
        services.TryAddSingleton<IRealtimeApi, RealtimeApi>();
        services.TryAddSingleton<RealtimeSubscriber>();
        services.TryAddSingleton<IInngestRealtime, InngestRealtime>();
        services.TryAddSingleton<IFunctionRegistry, FunctionRegistry>();
        services.TryAddSingleton<IExecutionEngine, ExecutionEngine>();
        services.TryAddSingleton(static provider => new InngestCommHandler(
            provider.GetRequiredService<InngestSettings>(),
            provider.GetRequiredService<IFunctionRegistry>(),
            provider.GetRequiredService<IExecutionEngine>(),
            provider.GetRequiredService<IInngestApi>(),
            provider.GetRequiredService<RequestSigner>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<ILogger<InngestCommHandler>>()));
        services.TryAddSingleton(static provider => new InngestSignatureVerifier(
            provider.GetRequiredService<InngestSettings>(),
            provider.GetRequiredService<RequestSigner>()));

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
