// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DotNet.Testcontainers.Containers;

namespace Inngest.Testing.Containers;

/// <summary>
/// An Inngest server in Docker, started with <see cref="InngestServerBuilder"/>: the dev server, or the self-hosted server
/// with its signing and event keys. <see cref="Configure"/> points an app at it and <see cref="SyncAsync"/> has it sync
/// an app that listens on the machine the test runs on.
/// </summary>
public sealed class InngestServerContainer : DockerContainer
{
    private readonly InngestServerConfiguration _configuration;
    private PortReservation? _reservation;

    /// <summary>
    /// Initializes a new instance of the <see cref="InngestServerContainer" /> class.
    /// </summary>
    /// <param name="configuration">The container configuration.</param>
    public InngestServerContainer(InngestServerConfiguration configuration)
        : base(configuration)
    {
        _configuration = configuration;
    }

    /// <summary>
    /// Gets whether this is the dev server, which signs nothing and takes any event key, and not the self-hosted server.
    /// </summary>
    public bool IsDevServer => !_configuration.SelfHosted;

    /// <summary>
    /// Gets the signing key of the self-hosted server, which signs every request it makes to an app and wants the key on
    /// its REST API. Null for the dev server.
    /// </summary>
    public string? SigningKey => _configuration.SelfHosted ? _configuration.SigningKey : null;

    /// <summary>
    /// Gets the event key of the self-hosted server, which events are sent with. Null for the dev server, which takes
    /// any event key.
    /// </summary>
    public string? EventKey => _configuration.SelfHosted ? _configuration.EventKey : null;

    /// <summary>
    /// Gets the port of the Connect gateway, which is the same inside the container and on the host. Null unless
    /// <see cref="InngestServerBuilder.WithConnectGateway"/> was called.
    /// </summary>
    public int? ConnectGatewayPort => _configuration.ConnectGateway ? _configuration.ConnectGatewayPort : null;

    internal InngestServerConfiguration Configuration => _configuration;

    // The gateway port the builder picked, kept from other processes until Docker is about to publish it.
    internal void Hold(PortReservation reservation) => _reservation = reservation;

    /// <summary>
    /// Gets the address of the server's API as the test reaches it: events, the REST and GraphQL APIs and the health
    /// check. The container must be running.
    /// </summary>
    /// <returns>The base address of the API.</returns>
    public Uri GetBaseAddress() =>
        new UriBuilder(Uri.UriSchemeHttp, Hostname, GetMappedPublicPort(InngestServerBuilder.InngestPort)).Uri;

    /// <summary>
    /// Sets the options of an app so that it uses this server: the dev server in dev mode, or the self-hosted server in
    /// cloud mode with its signing and event keys. Addresses set in code are replaced. The container must be running.
    /// </summary>
    /// <param name="options">The options of the app, for example in <c>AddInngest(inngest.Configure)</c>.</param>
    public void Configure(InngestOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        SdkSettings.Apply(options, GetBaseAddress(), _configuration);
    }

    /// <summary>
    /// Gets the same settings as <see cref="Configure"/> as the <c>INNGEST_*</c> variables the SDKs read, for an app that
    /// reads its configuration from the environment, such as a process the test starts. A process inherits the variables
    /// of the machine, so every variable that decides the mode or an address is set, and an inherited
    /// <c>INNGEST_DEV=1</c> or <c>INNGEST_BASE_URL</c> does not win: <c>INNGEST_DEV</c> is the dev server's address, or
    /// <c>0</c> for the self-hosted server, and <c>INNGEST_BASE_URL</c>, <c>INNGEST_API_BASE_URL</c> and
    /// <c>INNGEST_EVENT_API_BASE_URL</c> the address of the server. The self-hosted server's keys are
    /// <c>INNGEST_SIGNING_KEY</c> and <c>INNGEST_EVENT_KEY</c>. Other inherited <c>INNGEST_*</c> variables still apply.
    /// The container must be running.
    /// </summary>
    /// <returns>The variable names and values.</returns>
    public IReadOnlyDictionary<string, string> GetSdkEnvironment() => SdkSettings.ToEnvironment(GetBaseAddress(), _configuration);

    /// <summary>
    /// Has the server register the app that listens at this URL, which an SDK does by answering a PUT with its
    /// registration. The app registers the URL the request arrived on and the server then calls it, so the request
    /// names the address the container reaches the app by: <c>host.docker.internal</c> for a loopback address, which is
    /// the container itself in there, and any other address as given. Like the server's own sync, the request says which
    /// kind of server it comes from, and a self-hosted server's apps verify signatures, so it is signed with the signing
    /// key. Returns once the server knows the app's functions, and throws if the app refuses.
    /// </summary>
    /// <param name="appUrl">
    /// The URL of the app's Inngest endpoint as the test reaches it, such as <c>http://localhost:5000/api/inngest</c>.
    /// An app that listens on all interfaces may be named <c>http://[::]:5000/api/inngest</c>, as Kestrel reports it.
    /// </param>
    /// <param name="cancellationToken">A token that cancels the wait for the answer.</param>
    /// <returns>A task that completes when the app is registered.</returns>
    /// <exception cref="InvalidOperationException">The app answered, but did not register.</exception>
    /// <exception cref="HttpRequestException">Nothing answers at the address.</exception>
    public async Task SyncAsync(Uri appUrl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(appUrl);
        var urlForContainer = HostAccess.ForContainer(appUrl);

        using var request = new HttpRequestMessage(HttpMethod.Put, HostAccess.ForTest(appUrl));

        // The app registers the URL the request arrived on, which is built from the Host header.
        request.Headers.Host = urlForContainer.Authority;

        // The app passes the kind on to the server it registers with, which checks that it is that kind of server.
        request.Headers.Add("X-Inngest-Server-Kind", IsDevServer ? "dev" : "cloud");
        if (SigningKey is { } signingKey)
        {
            request.Headers.Add("X-Inngest-Signature", Sign(signingKey));
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"The app at {appUrl} did not register with the Inngest server: PUT answered {(int)response.StatusCode} {body}".TrimEnd());
        }
    }

    /// <summary>
    /// Starts the container. When the server does not become ready, the exception carries the server's log.
    /// </summary>
    /// <param name="ct">A token that cancels the start.</param>
    /// <returns>A task that completes when the server is ready.</returns>
    public override async Task StartAsync(CancellationToken ct = default)
    {
        try
        {
            await base.StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var log = await TryReadLogAsync().ConfigureAwait(false);
            if (log.Length == 0)
            {
                throw;
            }

            throw new InvalidOperationException($"The Inngest server ({Image.FullName}) did not become ready. Its log:\n{log}", exception);
        }
    }

    /// <inheritdoc />
    protected override Task UnsafeStartContainerAsync(CancellationToken ct = default)
    {
        // The container exists, so the image is pulled: let Docker have the port the builder picked for the gateway.
        _reservation?.Release();
        return base.UnsafeStartContainerAsync(ct);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        // A container that was built and never started still holds its gateway port.
        _reservation?.Release();
        await base.DisposeAsyncCore().ConfigureAwait(false);
    }

    // An empty request body signs as the timestamp alone: the signature is a MAC of the body followed by t.
    private static string Sign(string signingKey)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingKey), Encoding.ASCII.GetBytes(timestamp));
        return $"t={timestamp}&s={Convert.ToHexString(mac).ToLowerInvariant()}";
    }

    // The container may not exist, for example when the image could not be pulled, and then there is no log to add.
    private async Task<string> TryReadLogAsync()
    {
        try
        {
            var (stdout, stderr) = await GetLogsAsync(timestampsEnabled: false, ct: CancellationToken.None).ConfigureAwait(false);
            return (stdout + stderr).Trim();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return string.Empty;
        }
    }
}
