// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods that add an Inngest server to an AppHost and connect the apps of the AppHost to it.
/// </summary>
public static class InngestResourceBuilderExtensions
{
    // What the server listens on inside the container. Only the Connect gateway's port is configurable.
    private const int ApiPort = 8288;

    // The path the SDK serves on when the app maps it as in its documentation.
    private const string DefaultServePath = "/api/inngest";

    /// <summary>
    /// Adds the Inngest dev server (<c>inngest dev</c>) as a container. Its UI is linked in the dashboard, and apps
    /// find it through <see cref="WithReference{TDestination}(IResourceBuilder{TDestination}, IResourceBuilder{InngestServerResource})"/>.
    /// The server does not scan for apps; <see cref="WithApp{TApp}(IResourceBuilder{InngestServerResource}, IResourceBuilder{TApp}, string, string)"/> tells it where they are.
    /// </summary>
    /// <remarks>
    /// This version of the package runs the <c>v1.49.0</c> tag of the <c>inngest/inngest</c> image. Change it with
    /// <c>WithImageTag</c>.
    /// </remarks>
    /// <param name="builder">The AppHost builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="port">The host port of the API and UI. It is picked by Aspire when <see langword="null"/>.</param>
    /// <param name="connectPort">
    /// The port of the Connect gateway, which is the same on the host and in the container because the server hands
    /// workers its own port. It is picked by Aspire when <see langword="null"/>.
    /// </param>
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
    /// must be a hex string of even length, without a <c>signkey-</c> prefix.
    /// </remarks>
    /// <param name="builder">The AppHost builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="signingKey">A secret parameter with the signing key. One is generated when <see langword="null"/>.</param>
    /// <param name="eventKey">A secret parameter with the event key. One is generated when <see langword="null"/>.</param>
    /// <param name="port">The host port of the API and UI. It is picked by Aspire when <see langword="null"/>.</param>
    /// <param name="connectPort">
    /// The port of the Connect gateway, which is the same on the host and in the container because the server hands
    /// workers its own port. It is picked by Aspire when <see langword="null"/>.
    /// </param>
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
    /// <param name="builder">The Inngest server.</param>
    /// <param name="app">The app. It must have an endpoint the server can reach over HTTP.</param>
    /// <param name="path">The path the app serves Inngest on, <c>/api/inngest</c> by default.</param>
    /// <param name="endpointName">The name of the endpoint of the app, <c>http</c> by default. The server does not trust the development certificate of an HTTPS endpoint.</param>
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
    /// <c>INNGEST_DEV</c>, which turns on dev mode and holds the server's address, and <c>INNGEST_BASE_URL</c> with the
    /// same address for SDKs that read only that. For a self-hosted server it is <c>INNGEST_BASE_URL</c>,
    /// <c>INNGEST_SIGNING_KEY</c> and <c>INNGEST_EVENT_KEY</c>, with <c>INNGEST_DEV</c> set to <c>0</c> so that an
    /// <c>INNGEST_DEV</c> in the environment of the AppHost cannot turn off signature checks.
    /// </summary>
    /// <remarks>
    /// The address is worked out for the resource that gets it: a project on the host is given the address of the
    /// host port, and a container is given the address of the server on the container network. The resource does not
    /// wait for the server; add <c>WaitFor</c> if it must not start before the server is healthy.
    /// </remarks>
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

        return builder
            .WithReferenceRelationship(server)
            .WithEnvironment(context =>
            {
                if (server.IsSelfHosted)
                {
                    context.EnvironmentVariables["INNGEST_DEV"] = "0";
                    context.EnvironmentVariables["INNGEST_BASE_URL"] = address;
                    context.EnvironmentVariables["INNGEST_SIGNING_KEY"] = server.SigningKeyParameter!;
                    context.EnvironmentVariables["INNGEST_EVENT_KEY"] = server.EventKeyParameter!;
                }
                else
                {
                    context.EnvironmentVariables["INNGEST_DEV"] = address;
                    context.EnvironmentVariables["INNGEST_BASE_URL"] = address;
                }
            });
    }

    private static IResourceBuilder<InngestServerResource> AddServer(
        IDistributedApplicationBuilder builder,
        InngestServerResource resource,
        int? port,
        int? connectPort)
    {
        return builder.AddResource(resource)
            .WithImage(InngestContainerImageTags.Image, InngestContainerImageTags.Tag)
            .WithImageRegistry(InngestContainerImageTags.Registry)
            .WithIconName("Flash")
            .WithHttpEndpoint(port: port, targetPort: ApiPort, name: InngestServerResource.PrimaryEndpointName)
            // Workers dial the gateway at the host they called the API on and the port the server itself listens on,
            // so the port must be the same outside and inside the container. A proxy would hand the host a different
            // one; an endpoint without one makes Aspire pick a free port and use it on both sides.
            .WithHttpEndpoint(port: connectPort, targetPort: connectPort, name: InngestServerResource.ConnectEndpointName, isProxied: false)
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

                context.Args.Add("--connect-gateway-port");
                context.Args.Add(resource.ConnectEndpoint.Property(EndpointProperty.TargetPort));
            })
            .WithEnvironment(context =>
            {
                // Read from the environment rather than the arguments, so that the keys are not in the command line.
                if (resource.SigningKeyParameter is { } signingKey && resource.EventKeyParameter is { } eventKey)
                {
                    context.EnvironmentVariables["INNGEST_SIGNING_KEY"] = signingKey;
                    context.EnvironmentVariables["INNGEST_EVENT_KEY"] = eventKey;
                }
            });
    }
}
