// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Globalization;
using System.Security.Cryptography;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Images;

namespace Inngest.Testing.Containers;

/// <summary>
/// Builds an <see cref="InngestContainer"/> that runs the Inngest dev server, or with <see cref="WithSelfHosting"/> the
/// self-hosted server. The image is always the caller's choice, as for every Testcontainers module; the builder is
/// tested with <c>inngest/inngest:v1.49.0</c>, and passes command line flags that older images may not know.
/// </summary>
/// <remarks>
/// Both servers start ready to test with: <c>--no-discovery</c> keeps the dev server from scanning ports, and by default
/// the timing flags of <see cref="WithFastTiming"/> make runs finish quickly. The container reaches the machine the test
/// runs on as <c>host.docker.internal</c>, which Linux needs mapped to the host gateway; the builder does that. It is
/// ready when the API answers <c>/health</c> and, with <see cref="WithConnectGateway"/>, the gateway answers
/// <c>/ready</c>; a wait strategy with conditions of its own, set with <c>WithWaitStrategy</c>, replaces that.
/// </remarks>
public sealed class InngestBuilder : ContainerBuilder<InngestBuilder, InngestContainer, InngestConfiguration>
{
    /// <summary>
    /// The port of the server's API inside the container: events, the REST and GraphQL APIs, syncs and the health check.
    /// </summary>
    public const ushort InngestPort = 8288;

    private static readonly string[] FastTiming = ["--poll-interval", "1", "--retry-interval", "1", "--tick", "50"];

    /// <summary>
    /// Initializes a new instance of the <see cref="InngestBuilder" /> class.
    /// </summary>
    /// <param name="image">
    /// The full Docker image name, including the image repository and tag
    /// (e.g., <c>inngest/inngest:v1.49.0</c>).
    /// </param>
    /// <remarks>
    /// Docker image tags available at <see href="https://hub.docker.com/r/inngest/inngest/tags" />.
    /// </remarks>
    public InngestBuilder(string image)
        : this(new DockerImage(image))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="InngestBuilder" /> class.
    /// </summary>
    /// <param name="image">
    /// An <see cref="IImage" /> instance that specifies the Docker image to be used
    /// for the container builder configuration.
    /// </param>
    /// <remarks>
    /// Docker image tags available at <see href="https://hub.docker.com/r/inngest/inngest/tags" />.
    /// </remarks>
    public InngestBuilder(IImage image)
        : this(new InngestConfiguration())
    {
        DockerResourceConfiguration = Init().WithImage(image).DockerResourceConfiguration;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="InngestBuilder" /> class.
    /// </summary>
    /// <param name="resourceConfiguration">The Docker resource configuration.</param>
    private InngestBuilder(InngestConfiguration resourceConfiguration)
        : base(resourceConfiguration)
    {
        DockerResourceConfiguration = resourceConfiguration;
    }

    /// <inheritdoc />
    protected override InngestConfiguration DockerResourceConfiguration { get; }

    /// <summary>
    /// Runs the self-hosted server (<c>inngest start</c>) in place of the dev server. It signs every request it makes to
    /// the app and wants the signing key on its REST API, so an app in cloud mode with the same keys exercises request
    /// signing for real. A key the builder is not given is generated and read back from the container.
    /// </summary>
    /// <returns>A configured instance of <see cref="InngestBuilder" />.</returns>
    public InngestBuilder WithSelfHosting() => With(new InngestConfiguration(selfHosted: true));

    /// <summary>
    /// Sets the signing key of the self-hosted server, which <c>inngest start</c> accepts only as hexadecimal digits in
    /// pairs, without the <c>signkey-env-</c> prefix of Inngest Cloud keys.
    /// </summary>
    /// <param name="signingKey">The signing key.</param>
    /// <returns>A configured instance of <see cref="InngestBuilder" />.</returns>
    public InngestBuilder WithSigningKey(string signingKey) => With(new InngestConfiguration(signingKey: signingKey));

    /// <summary>
    /// Sets the event key of the self-hosted server. The dev server takes any event key.
    /// </summary>
    /// <param name="eventKey">The event key.</param>
    /// <returns>A configured instance of <see cref="InngestBuilder" />.</returns>
    public InngestBuilder WithEventKey(string eventKey) => With(new InngestConfiguration(eventKey: eventKey));

    /// <summary>
    /// Has the server sync the app at this URL when it starts, and retry every second until the app answers, so the app
    /// can start before or after the server. The URL is the one the test reaches the app on: a loopback address is
    /// replaced by <c>host.docker.internal</c>, because inside the container localhost is the container. Call it again
    /// for more apps. An app that is not known when the container starts can be synced later with
    /// <see cref="InngestContainer.SyncAsync"/>.
    /// </summary>
    /// <param name="appUrl">The URL of the app's Inngest endpoint, such as <c>http://localhost:5000/api/inngest</c>.</param>
    /// <returns>A configured instance of <see cref="InngestBuilder" />.</returns>
    public InngestBuilder WithAppUrl(Uri appUrl) =>
        With(new InngestConfiguration(appUrls: [HostAccess.ForContainer(appUrl).AbsoluteUri]));

    /// <inheritdoc cref="WithAppUrl(Uri)" />
    public InngestBuilder WithAppUrl(string appUrl) => WithAppUrl(new Uri(appUrl, UriKind.Absolute));

    /// <summary>
    /// Publishes the Connect gateway, which the workers of <c>Inngest.Connect</c> open their WebSocket to. The start API
    /// hands workers the gateway's port as it is inside the container, so the same port is published on the host. The
    /// container is ready only once the gateway is too.
    /// </summary>
    /// <param name="port">
    /// The port inside the container and on the host. A free port of the host is picked when the container is built if
    /// none is given.
    /// </param>
    /// <returns>A configured instance of <see cref="InngestBuilder" />.</returns>
    public InngestBuilder WithConnectGateway(int? port = null) =>
        With(new InngestConfiguration(connectGateway: true, connectGatewayPort: port));

    /// <summary>
    /// Turns the timing flags on or off. They are on by default, because a test should not wait out the delays of a
    /// production server: a sync that failed is tried again after a second, a failed step is retried after a second
    /// instead of the server's backoff of 15 seconds and more, and the queue is polled every 50 ms instead of every
    /// 150 ms, which is also how far apart consecutive steps run.
    /// </summary>
    /// <param name="fastTiming">False leaves the server with its own timing.</param>
    /// <returns>A configured instance of <see cref="InngestBuilder" />.</returns>
    public InngestBuilder WithFastTiming(bool fastTiming = true) => With(new InngestConfiguration(fastTiming: fastTiming));

    /// <inheritdoc />
    /// <remarks>
    /// The command is built from the settings here, with whatever <c>WithCommand</c> added appended to it.
    /// </remarks>
    public override InngestContainer Build()
    {
        Validate();

        var configuration = DockerResourceConfiguration;
        var signingKey = configuration.SelfHosted ? configuration.SigningKey ?? RandomHex(32) : null;
        var eventKey = configuration.SelfHosted ? configuration.EventKey ?? $"test-{RandomHex(8)}" : null;
        int? gatewayPort = configuration.ConnectGateway ? configuration.ConnectGatewayPort ?? FreePort.Pick() : null;

        var builder = With(new InngestConfiguration(signingKey: signingKey, eventKey: eventKey, connectGatewayPort: gatewayPort))
            .WithCommand(new OverwriteEnumerable<string>(CreateCommand(configuration, signingKey, eventKey, gatewayPort)))
            .WithCreateParameterModifier(parameters => ReserveServerPorts(parameters, gatewayPort));

        if (gatewayPort is { } port)
        {
            builder = builder.WithPortBinding(port, port);
        }

        // A wait strategy replaces the one before it, and one with conditions of its own is the caller's: the default
        // is only the check that the container runs.
        if (builder.DockerResourceConfiguration.WaitStrategies.Count() <= 1)
        {
            var ready = UntilReady(InngestPort, "/health");
            builder = builder.WithWaitStrategy(gatewayPort is { } gateway ? ready.UntilHttpRequestIsSucceeded(request => request.ForPort((ushort)gateway).ForPath("/ready"), Polling) : ready);
        }

        return new InngestContainer(builder.DockerResourceConfiguration);
    }

    /// <inheritdoc />
    protected override InngestBuilder Init()
    {
        return base.Init()
            .WithPortBinding(InngestPort, true)
            // Docker Desktop resolves host.docker.internal on its own; Linux needs it mapped to the host gateway.
            .WithExtraHost(HostAccess.ContainerHostName, "host-gateway")
            .WithEnvironment("DO_NOT_TRACK", "1")
            .WithConnectionStringProvider(new InngestConnectionStringProvider());
    }

    /// <inheritdoc />
    protected override void Validate()
    {
        base.Validate();

        var configuration = DockerResourceConfiguration;
        if (!configuration.SelfHosted && (configuration.SigningKey is not null || configuration.EventKey is not null))
        {
            throw new ArgumentException(
                "A signing key and an event key belong to the self-hosted server: call WithSelfHosting, or leave them out, since the dev server signs nothing and takes any event key.",
                nameof(configuration));
        }

        if (configuration.SigningKey is { } signingKey && !IsPairsOfHexDigits(signingKey))
        {
            throw new ArgumentException(
                "inngest start only accepts a signing key of hexadecimal digits in pairs, without a signkey-env- prefix: with one it refuses to start, or signs syncs and calls with different keys.",
                nameof(configuration));
        }

        if (configuration.EventKey is { } eventKey && string.IsNullOrWhiteSpace(eventKey))
        {
            throw new ArgumentException("The event key cannot be empty.", nameof(configuration));
        }

        if (configuration.ConnectGatewayPort is { } gatewayPort && (gatewayPort is < 1 or > ushort.MaxValue || gatewayPort == InngestPort))
        {
            throw new ArgumentException(
                $"The Connect gateway port must be a port other than {InngestPort.ToString(CultureInfo.InvariantCulture)}, the server's API.",
                nameof(configuration));
        }
    }

    /// <inheritdoc />
    protected override InngestBuilder Clone(IResourceConfiguration<CreateContainerParameters> resourceConfiguration)
    {
        return Merge(DockerResourceConfiguration, new InngestConfiguration(resourceConfiguration));
    }

    /// <inheritdoc />
    protected override InngestBuilder Clone(IContainerConfiguration resourceConfiguration)
    {
        return Merge(DockerResourceConfiguration, new InngestConfiguration(resourceConfiguration));
    }

    /// <inheritdoc />
    protected override InngestBuilder Merge(InngestConfiguration oldValue, InngestConfiguration newValue)
    {
        return new InngestBuilder(new InngestConfiguration(oldValue, newValue));
    }

    private InngestBuilder With(InngestConfiguration change) => Merge(DockerResourceConfiguration, change);

    // The server is up about 0.25 s after it starts, so the default interval of a second would add most of a second.
    private static void Polling(IWaitStrategy wait) =>
        wait.WithInterval(TimeSpan.FromMilliseconds(100)).WithTimeout(TimeSpan.FromSeconds(60));

    private static IWaitForContainerOS UntilReady(ushort port, string path) =>
        Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(port).ForPath(path), Polling);

    private static List<string> CreateCommand(InngestConfiguration configuration, string? signingKey, string? eventKey, int? gatewayPort)
    {
        List<string> command = ["inngest"];
        if (configuration.SelfHosted)
        {
            command.AddRange(["start", "--signing-key", signingKey!, "--event-key", eventKey!]);
        }
        else
        {
            // Without --no-discovery the dev server scans ports inside the container, and with --no-poll it syncs an
            // app once, not every few seconds.
            command.AddRange(["dev", "--no-discovery", "--no-poll"]);
        }

        if (configuration.FastTiming)
        {
            command.AddRange(FastTiming);
        }

        if (gatewayPort is { } port)
        {
            command.AddRange(["--connect-gateway-port", port.ToString(CultureInfo.InvariantCulture)]);
        }

        foreach (var appUrl in configuration.AppUrls)
        {
            command.AddRange(["-u", appUrl]);
        }

        if (configuration.Command is { } extraArguments)
        {
            command.AddRange(extraArguments);
        }

        return command;
    }

    // The server binds its Connect gRPC ports and the gateway port only after it has opened connections, and the kernel
    // may hand one of those connections such a port as its source port; the start then fails with "address already in
    // use". The kernel never hands out a reserved port.
    private static void ReserveServerPorts(CreateContainerParameters parameters, int? gatewayPort)
    {
        var hostConfig = parameters.HostConfig ??= new HostConfig();
        hostConfig.Sysctls ??= new Dictionary<string, string>();
        hostConfig.Sysctls["net.ipv4.ip_local_reserved_ports"] = gatewayPort is { } port
            ? $"50052-50053,{port.ToString(CultureInfo.InvariantCulture)}"
            : "50052-50053";
    }

    private static string RandomHex(int byteCount) => Convert.ToHexString(RandomNumberGenerator.GetBytes(byteCount)).ToLowerInvariant();

    private static bool IsPairsOfHexDigits(string text) =>
        text.Length > 0 && text.Length % 2 == 0 && text.All(char.IsAsciiHexDigit);
}
