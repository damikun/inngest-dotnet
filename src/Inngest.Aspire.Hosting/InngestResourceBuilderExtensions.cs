// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods that add an Inngest server to an AppHost and connect the apps of the AppHost to it.
/// </summary>
public static class InngestResourceBuilderExtensions
{
    // What the server listens on inside the container, which are the ports it defaults to.
    private const int ApiPort = 8288;
    private const int GatewayPort = 8289;

    // The path the SDK serves on when the app maps it as in its documentation.
    private const string DefaultServePath = "/api/inngest";

    /// <summary>
    /// Adds the Inngest dev server (<c>inngest dev</c>) as a container. Its UI is linked in the dashboard, and apps
    /// find it through <see cref="WithReference{TDestination}(IResourceBuilder{TDestination}, IResourceBuilder{InngestServerResource})"/>.
    /// The server does not scan for apps; <see cref="WithApp{TApp}(IResourceBuilder{InngestServerResource}, IResourceBuilder{TApp}, string, string)"/> tells it where they are.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This version of the package runs the <c>v1.49.0</c> tag of the <c>inngest/inngest</c> image. Change it with
    /// <c>WithImageTag</c>.
    /// </para>
    /// <para>
    /// The dev server checks no signatures, so it is left out of a manifest, and <c>WithReference</c> adds no settings
    /// to one. An app that is deployed from the manifest runs in cloud mode and takes its keys from where it is
    /// deployed.
    /// </para>
    /// </remarks>
    /// <param name="builder">The AppHost builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="port">The host port of the API and UI. It is picked by Aspire when <see langword="null"/>.</param>
    /// <param name="connectPort">The host port of the Connect gateway. It is picked by Aspire when <see langword="null"/>.</param>
    public static IResourceBuilder<InngestServerResource> AddInngest(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        int? port = null,
        int? connectPort = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        return AddServer(builder, new InngestServerResource(name), port, connectPort);
    }

    /// <summary>
    /// Adds a self-hosted Inngest server (<c>inngest start</c>) as a container. It signs every request to an app and
    /// wants the signing key on its API and an event key on every event, so it exercises the way an app runs in
    /// production. Projects that reference it get both keys.
    /// </summary>
    /// <remarks>
    /// The server keeps its state in the container: it adds no volume, database or Redis. Keys that are not given are
    /// generated and kept in the user secrets of the AppHost, as Aspire does for passwords. A signing key you give
    /// must be a hex string of even length, without a <c>signkey-</c> prefix. Unlike the dev server, the self-hosted
    /// server is part of a manifest, with its keys as parameters.
    /// </remarks>
    /// <param name="builder">The AppHost builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="signingKey">A secret parameter with the signing key. One is generated when <see langword="null"/>.</param>
    /// <param name="eventKey">A secret parameter with the event key. One is generated when <see langword="null"/>.</param>
    /// <param name="port">The host port of the API and UI. It is picked by Aspire when <see langword="null"/>.</param>
    /// <param name="connectPort">The host port of the Connect gateway. It is picked by Aspire when <see langword="null"/>.</param>
    public static IResourceBuilder<InngestServerResource> AddInngestSelfHosted(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        IResourceBuilder<ParameterResource>? signingKey = null,
        IResourceBuilder<ParameterResource>? eventKey = null,
        int? port = null,
        int? connectPort = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        // inngest start accepts only hex of even length as the signing key. Digits are hex, so 64 of them make a key with
        // about 210 bits, and the generator of parameters cannot be limited to a to f, as it can to digits.
        var signingKeyParameter = signingKey?.Resource
            ?? ParameterResourceBuilderExtensions.CreateGeneratedParameter(
                builder,
                $"{name}-signing-key",
                secret: true,
                new GenerateParameterDefault { MinLength = 64, Lower = false, Upper = false, Special = false });

        // The event key is part of the URL events are posted to, so it has no special characters.
        var eventKeyParameter = eventKey?.Resource
            ?? ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(builder, $"{name}-event-key", special: false);

        return AddServer(builder, new InngestServerResource(name, signingKeyParameter, eventKeyParameter), port, connectPort);
    }

    /// <summary>
    /// Makes the Inngest server sync an app that serves functions, now and whenever it changes. The server calls the
    /// app at the address the container reaches it on, which Aspire works out: the host or tunnel address of a
    /// project that runs on the host, and the network name of an app that is a container.
    /// </summary>
    /// <remarks>
    /// Apps that only send events, and Connect workers, need no registration. The server keeps trying until the app
    /// answers, so neither has to wait for the other.
    /// </remarks>
    /// <typeparam name="TApp">The type of the app resource.</typeparam>
    /// <param name="builder">The Inngest server.</param>
    /// <param name="app">The app. It must have an endpoint the server can reach over HTTP.</param>
    /// <param name="path">The path the app serves Inngest on, <c>/api/inngest</c> by default.</param>
    /// <param name="endpointName">
    /// The name of the endpoint of the app, <c>http</c> by default. An <c>https</c> endpoint of a project uses the ASP.NET
    /// Core development certificate, which the container of the server does not trust unless it is set up to.
    /// </param>
    public static IResourceBuilder<InngestServerResource> WithApp<TApp>(
        this IResourceBuilder<InngestServerResource> builder,
        IResourceBuilder<TApp> app,
        string path = DefaultServePath,
        string endpointName = InngestServerResource.PrimaryEndpointName)
        where TApp : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(app);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrEmpty(endpointName);

        var servePath = path.StartsWith('/') ? path : "/" + path;
        var address = app.Resource.GetEndpoint(endpointName).Property(EndpointProperty.Url);
        var serveUrl = ReferenceExpression.Create($"{address}{servePath}");

        // The server takes -u once for every app. The arguments of this callback follow those of AddServer.
        return builder
            .WithReferenceRelationship(app.Resource)
            .WithArgs(context =>
            {
                context.Args.Add("-u");
                context.Args.Add(serveUrl);
            });
    }

    /// <summary>
    /// Gives the resource the settings the Inngest SDK reads to find the server. For the dev server that is
    /// <c>INNGEST_DEV</c>, which turns on dev mode and holds the server's address. For a self-hosted server it is
    /// <c>INNGEST_SIGNING_KEY</c> and <c>INNGEST_EVENT_KEY</c>, with <c>INNGEST_DEV</c> set to <c>0</c> so that an
    /// <c>INNGEST_DEV</c> in the environment of the AppHost cannot turn off signature checks. Both get the address in
    /// <c>INNGEST_BASE_URL</c>, <c>INNGEST_API_BASE_URL</c> and <c>INNGEST_EVENT_API_BASE_URL</c>, and the address of
    /// the Connect gateway in <c>INNGEST_CONNECT_GATEWAY_URL</c>, for workers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The addresses are worked out for the resource that gets them: a project on the host is given the address of the
    /// host port, and a container is given the address of the server on the container network. The resource does not
    /// wait for the server; add <c>WaitFor</c> if it must not start before the server is healthy.
    /// </para>
    /// <para>
    /// The SDK reads <c>INNGEST_API_BASE_URL</c> and <c>INNGEST_EVENT_API_BASE_URL</c> before <c>INNGEST_BASE_URL</c>, and
    /// a resource inherits the environment of the AppHost, so both are set to keep an address from there out. Other
    /// <c>INNGEST_*</c> variables of that environment still reach the resource.
    /// </para>
    /// <para>
    /// The server tells a worker to dial the gateway on the port that it listens on in the container, which is not
    /// the port that Aspire publishes it on. The worker takes the address of the gateway from
    /// <c>INNGEST_CONNECT_GATEWAY_URL</c> instead.
    /// </para>
    /// <para>
    /// A manifest has these settings for a self-hosted server only. The dev server is not in a manifest, so a
    /// reference to it adds nothing to one.
    /// </para>
    /// </remarks>
    /// <typeparam name="TDestination">The type of the resource that gets the settings.</typeparam>
    /// <param name="builder">The resource that gets the settings, such as a project.</param>
    /// <param name="inngest">The Inngest server.</param>
    public static IResourceBuilder<TDestination> WithReference<TDestination>(
        this IResourceBuilder<TDestination> builder,
        IResourceBuilder<InngestServerResource> inngest)
        where TDestination : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(inngest);

        var server = inngest.Resource;
        var address = ReferenceExpression.Create($"{server.PrimaryEndpoint.Property(EndpointProperty.Url)}");
        var gateway = ReferenceExpression.Create($"ws://{server.ConnectEndpoint.Property(EndpointProperty.HostAndPort)}/v0/connect");

        return builder
            .WithReferenceRelationship(server)
            .WithEnvironment(context =>
            {
                // The dev server is not in a manifest. Whatever refers to it there would dangle, and INNGEST_DEV would put
                // the deployed app into dev mode, which checks no signatures.
                if (!server.IsSelfHosted && context.ExecutionContext.IsPublishMode)
                {
                    return;
                }

                if (server.IsSelfHosted)
                {
                    context.EnvironmentVariables["INNGEST_DEV"] = "0";
                    context.EnvironmentVariables["INNGEST_SIGNING_KEY"] = server.SigningKeyParameter!;
                    context.EnvironmentVariables["INNGEST_EVENT_KEY"] = server.EventKeyParameter!;
                }
                else
                {
                    context.EnvironmentVariables["INNGEST_DEV"] = address;
                }

                // The SDK reads the API and event API addresses before the base URL, and the environment of the AppHost
                // may have set them.
                context.EnvironmentVariables["INNGEST_BASE_URL"] = address;
                context.EnvironmentVariables["INNGEST_API_BASE_URL"] = address;
                context.EnvironmentVariables["INNGEST_EVENT_API_BASE_URL"] = address;
                context.EnvironmentVariables["INNGEST_CONNECT_GATEWAY_URL"] = gateway;
            });
    }

    private static IResourceBuilder<InngestServerResource> AddServer(
        IDistributedApplicationBuilder builder,
        InngestServerResource resource,
        int? port,
        int? connectPort)
    {
        var server = builder.AddResource(resource)
            .WithImage(InngestContainerImageTags.Image, InngestContainerImageTags.Tag)
            .WithImageRegistry(InngestContainerImageTags.Registry)
            .WithIconName("Flash")
            .WithHttpEndpoint(port: port, targetPort: ApiPort, name: InngestServerResource.PrimaryEndpointName)
            // The gateway speaks HTTP until a worker upgrades the connection to a WebSocket on /v0/connect, which is
            // what Aspire's health check needs.
            .WithHttpEndpoint(port: connectPort, targetPort: GatewayPort, name: InngestServerResource.ConnectEndpointName)
            .WithHttpHealthCheck("/health", endpointName: InngestServerResource.PrimaryEndpointName)
            .WithHttpHealthCheck("/ready", endpointName: InngestServerResource.ConnectEndpointName)
            .WithUrlForEndpoint(InngestServerResource.PrimaryEndpointName, url => url.DisplayText = "Inngest UI")
            .WithUrlForEndpoint(InngestServerResource.ConnectEndpointName, url => url.DisplayLocation = UrlDisplayLocation.DetailsOnly)
            .WithArgs(context =>
            {
                if (resource.IsSelfHosted)
                {
                    context.Args.Add("start");

                    // The server polls an app only when told to, and an app that starts after the server must be found.
                    context.Args.Add("--poll-interval");
                    context.Args.Add("5");
                }
                else
                {
                    context.Args.Add("dev");

                    // Inside the container, discovery would only scan the container's own ports.
                    context.Args.Add("--no-discovery");
                }
            })
            .WithEnvironment(async context =>
            {
                // Read from the environment rather than the arguments, so that the keys are not in the command line.
                if (resource.SigningKeyParameter is { } signingKey && resource.EventKeyParameter is { } eventKey)
                {
                    if (context.ExecutionContext.IsRunMode)
                    {
                        await RequireKeysAsync(resource, signingKey, eventKey, context.CancellationToken).ConfigureAwait(false);
                    }

                    context.EnvironmentVariables["INNGEST_SIGNING_KEY"] = signingKey;
                    context.EnvironmentVariables["INNGEST_EVENT_KEY"] = eventKey;
                }
            });

        // The dev server is a tool for the developer's machine. A manifest that ran it would give a deployed app a server
        // that checks no signatures.
        return resource.IsSelfHosted ? server : server.ExcludeFromManifest();
    }

    // inngest start exits when the signing key is not hex of even length, which shows up as a container that stopped. A key
    // with a signkey- prefix, as Inngest Cloud hands out, is the likely mistake, so the AppHost says what is wrong instead.
    private static async Task RequireKeysAsync(InngestServerResource resource, ParameterResource signingKey, ParameterResource eventKey, CancellationToken cancellationToken)
    {
        var signingKeyValue = await signingKey.GetValueAsync(cancellationToken).ConfigureAwait(false);
        if (signingKeyValue is not { Length: > 0 } || signingKeyValue.Length % 2 != 0 || !signingKeyValue.All(char.IsAsciiHexDigit))
        {
            throw new DistributedApplicationException(
                $"The signing key of the Inngest server '{resource.Name}' (parameter '{signingKey.Name}') must be a hex string of even length without a 'signkey-' prefix, which is all that 'inngest start' accepts.");
        }

        if (string.IsNullOrWhiteSpace(await eventKey.GetValueAsync(cancellationToken).ConfigureAwait(false)))
        {
            throw new DistributedApplicationException($"The event key of the Inngest server '{resource.Name}' (parameter '{eventKey.Name}') must not be empty.");
        }
    }
}
