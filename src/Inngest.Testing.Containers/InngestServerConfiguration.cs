// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;

namespace Inngest.Testing.Containers;

/// <summary>
/// The configuration of an <see cref="InngestServerContainer"/>: what any container has, and what the
/// <see cref="InngestServerBuilder"/> keeps for the server it starts. The builder turns it into a command line when it builds
/// the container, so the order of its methods never matters.
/// </summary>
public sealed class InngestServerConfiguration : ContainerConfiguration
{
    private readonly bool? _selfHosted;
    private readonly bool? _connectGateway;
    private readonly bool? _fastTiming;

    /// <summary>
    /// Initializes a new instance of the <see cref="InngestServerConfiguration" /> class.
    /// </summary>
    /// <param name="selfHosted">Runs <c>inngest start</c> in place of <c>inngest dev</c>.</param>
    /// <param name="signingKey">The signing key of the self-hosted server.</param>
    /// <param name="eventKey">The event key of the self-hosted server.</param>
    /// <param name="appUrls">The URLs of the apps to sync, as the container reaches them.</param>
    /// <param name="connectGateway">Publishes the Connect gateway.</param>
    /// <param name="connectGatewayPort">The port of the Connect gateway, inside the container and on the host.</param>
    /// <param name="fastTiming">Starts the server with the flags that make runs finish quickly.</param>
    public InngestServerConfiguration(
        bool? selfHosted = null,
        string? signingKey = null,
        string? eventKey = null,
        IReadOnlyList<string>? appUrls = null,
        bool? connectGateway = null,
        int? connectGatewayPort = null,
        bool? fastTiming = null)
    {
        _selfHosted = selfHosted;
        SigningKey = signingKey;
        EventKey = eventKey;
        AppUrls = appUrls ?? [];
        _connectGateway = connectGateway;
        ConnectGatewayPort = connectGatewayPort;
        _fastTiming = fastTiming;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="InngestServerConfiguration" /> class.
    /// </summary>
    /// <param name="resourceConfiguration">The Docker resource configuration.</param>
    public InngestServerConfiguration(IResourceConfiguration<CreateContainerParameters> resourceConfiguration)
        : base(resourceConfiguration)
    {
        // Passes the configuration upwards to the base implementations to create an updated immutable copy.
        AppUrls = [];
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="InngestServerConfiguration" /> class.
    /// </summary>
    /// <param name="resourceConfiguration">The Docker resource configuration.</param>
    public InngestServerConfiguration(IContainerConfiguration resourceConfiguration)
        : base(resourceConfiguration)
    {
        // Passes the configuration upwards to the base implementations to create an updated immutable copy.
        AppUrls = [];
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="InngestServerConfiguration" /> class.
    /// </summary>
    /// <param name="resourceConfiguration">The Docker resource configuration.</param>
    public InngestServerConfiguration(InngestServerConfiguration resourceConfiguration)
        : this(new InngestServerConfiguration(), resourceConfiguration)
    {
        // Passes the configuration upwards to the base implementations to create an updated immutable copy.
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="InngestServerConfiguration" /> class.
    /// </summary>
    /// <param name="oldValue">The old Docker resource configuration.</param>
    /// <param name="newValue">The new Docker resource configuration.</param>
    public InngestServerConfiguration(InngestServerConfiguration oldValue, InngestServerConfiguration newValue)
        : base(oldValue, newValue)
    {
        _selfHosted = BuildConfiguration.Combine(oldValue._selfHosted, newValue._selfHosted);
        SigningKey = BuildConfiguration.Combine(oldValue.SigningKey, newValue.SigningKey);
        EventKey = BuildConfiguration.Combine(oldValue.EventKey, newValue.EventKey);
        AppUrls = BuildConfiguration.Combine(oldValue.AppUrls, newValue.AppUrls);
        _connectGateway = BuildConfiguration.Combine(oldValue._connectGateway, newValue._connectGateway);
        ConnectGatewayPort = BuildConfiguration.Combine(oldValue.ConnectGatewayPort, newValue.ConnectGatewayPort);
        _fastTiming = BuildConfiguration.Combine(oldValue._fastTiming, newValue._fastTiming);
    }

    /// <summary>
    /// Gets whether the container runs the self-hosted server (<c>inngest start</c>) instead of the dev server.
    /// </summary>
    public bool SelfHosted => _selfHosted == true;

    /// <summary>
    /// Gets the signing key of the self-hosted server. Null until the builder sets or generates one.
    /// </summary>
    public string? SigningKey { get; }

    /// <summary>
    /// Gets the event key of the self-hosted server. Null until the builder sets or generates one.
    /// </summary>
    public string? EventKey { get; }

    /// <summary>
    /// Gets the URLs of the apps the server syncs when it starts, as the container reaches them.
    /// </summary>
    public IReadOnlyList<string> AppUrls { get; }

    /// <summary>
    /// Gets whether the Connect gateway is published.
    /// </summary>
    public bool ConnectGateway => _connectGateway == true;

    /// <summary>
    /// Gets the port of the Connect gateway. Null until the builder sets or picks one.
    /// </summary>
    public int? ConnectGatewayPort { get; }

    /// <summary>
    /// Gets whether the server starts with the flags that make runs finish quickly. True unless turned off.
    /// </summary>
    public bool FastTiming => _fastTiming != false;
}
