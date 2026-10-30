// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// An Inngest server that runs as a container: the dev server, or a self-hosted server that signs its requests and
/// wants event keys. It serves the REST API, the event API and the UI on <see cref="PrimaryEndpoint"/>, and the
/// gateway that Connect workers dial on <see cref="ConnectEndpoint"/>.
/// </summary>
public sealed class InngestServerResource : ContainerResource
{
    internal const string PrimaryEndpointName = "http";
    internal const string ConnectEndpointName = "connect";

    private EndpointReference? _primaryEndpoint;
    private EndpointReference? _connectEndpoint;

    /// <summary>
    /// Initializes the resource. Give it both keys for a self-hosted server and neither for the dev server.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="signingKey">The parameter with the signing key, which must be a hex string of even length, or <see langword="null"/> for the dev server.</param>
    /// <param name="eventKey">The parameter with the event key that apps send events with, or <see langword="null"/> for the dev server.</param>
    public InngestServerResource(string name, ParameterResource? signingKey = null, ParameterResource? eventKey = null)
        : base(name, "inngest")
    {
        if ((signingKey is null) != (eventKey is null))
        {
            throw new ArgumentException("A self-hosted Inngest server needs a signing key and an event key, and the dev server takes neither.", signingKey is null ? nameof(signingKey) : nameof(eventKey));
        }

        SigningKeyParameter = signingKey;
        EventKeyParameter = eventKey;
    }

    /// <summary>
    /// Gets whether this is a self-hosted server (<c>inngest start</c>) rather than the dev server (<c>inngest dev</c>).
    /// </summary>
    public bool IsSelfHosted => SigningKeyParameter is not null;

    /// <summary>
    /// Gets the parameter with the signing key of a self-hosted server, or <see langword="null"/> for the dev server.
    /// </summary>
    public ParameterResource? SigningKeyParameter { get; }

    /// <summary>
    /// Gets the parameter with the event key of a self-hosted server, or <see langword="null"/> for the dev server.
    /// </summary>
    public ParameterResource? EventKeyParameter { get; }

    /// <summary>
    /// Gets the endpoint of the REST API, the event API and the UI.
    /// </summary>
    public EndpointReference PrimaryEndpoint => _primaryEndpoint ??= new(this, PrimaryEndpointName);

    /// <summary>
    /// Gets the endpoint of the gateway that Connect workers dial. A resource that references the server gets its
    /// address as <c>INNGEST_CONNECT_GATEWAY_URL</c>.
    /// </summary>
    public EndpointReference ConnectEndpoint => _connectEndpoint ??= new(this, ConnectEndpointName);
}
