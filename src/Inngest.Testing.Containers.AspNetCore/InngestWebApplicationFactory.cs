// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Inngest.Testing.Containers;

/// <summary>
/// A <see cref="WebApplicationFactory{TEntryPoint}"/> that runs the app on Kestrel and points it at an Inngest server in
/// a container, so the server can call the app's functions over a real socket, which the factory's in-memory server
/// cannot offer. The app is configured with <see cref="InngestContainer.Configure"/>, whatever it set for itself, and
/// <see cref="StartAsync"/> starts it and has the server sync it. Requests the test sends with
/// <see cref="WebApplicationFactory{TEntryPoint}.CreateClient()"/> reach the same app over HTTP too.
/// </summary>
/// <remarks>
/// <para>
/// Start the container first: the app's settings hold the server's address, which is only known then. Customize the app
/// as for any factory, by overriding <c>ConfigureWebHost</c>. A factory made with <c>WithWebHostBuilder</c> keeps the
/// Inngest settings but runs on the in-memory server again, as ASP.NET Core does not carry the Kestrel choice over to it.
/// </para>
/// <para>
/// The server calls the app over plain HTTP, so an app that redirects to HTTPS needs that turned off in the test. One
/// container serves one instance of an app id: syncing another app with the same id replaces the first.
/// </para>
/// </remarks>
/// <typeparam name="TEntryPoint">A type of the app's assembly, typically its <c>Program</c> class.</typeparam>
public class InngestWebApplicationFactory<TEntryPoint> : WebApplicationFactory<TEntryPoint>
    where TEntryPoint : class
{
    private readonly string _servePath;

    /// <summary>
    /// Initializes a new instance of the <see cref="InngestWebApplicationFactory{TEntryPoint}" /> class.
    /// </summary>
    /// <param name="inngest">The server the app uses. It must be running before the app starts.</param>
    /// <param name="servePath">The path the app maps the Inngest endpoint at, <c>/api/inngest</c> unless <c>MapInngest</c> says otherwise.</param>
    public InngestWebApplicationFactory(InngestContainer inngest, string servePath = "/api/inngest")
    {
        ArgumentNullException.ThrowIfNull(inngest);
        ArgumentException.ThrowIfNullOrWhiteSpace(servePath);
        Container = inngest;
        _servePath = servePath.StartsWith('/') ? servePath : "/" + servePath;

        // All interfaces, not loopback: on Linux the container connects through the Docker bridge. The system picks the
        // port, so parallel tests never collide.
        UseKestrel(kestrel => kestrel.ListenAnyIP(0));
    }

    /// <summary>
    /// Gets the server the app is configured for.
    /// </summary>
    public InngestContainer Container { get; }

    /// <summary>
    /// Starts the app on Kestrel, if it is not running yet, and has the server sync it, so that the server knows its
    /// functions when this returns. Call it before creating clients.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the wait for the sync.</param>
    /// <returns>A task that completes when the server has registered the app.</returns>
    /// <exception cref="InvalidOperationException">The container is not running, or the app did not register.</exception>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        StartServer();

        // Kestrel reports all interfaces as [::], which clients can connect to on Linux and macOS but not on Windows.
        ClientOptions.BaseAddress = new UriBuilder(ClientOptions.BaseAddress) { Host = "localhost" }.Uri;
        await Container.SyncAsync(GetAppUrl(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the address of the app's Inngest endpoint as the test reaches it. The app starts if it is not running.
    /// </summary>
    /// <returns>The address of the endpoint on the port Kestrel was given.</returns>
    public Uri GetAppUrl()
    {
        StartServer();
        return new UriBuilder(Uri.UriSchemeHttp, "localhost", ClientOptions.BaseAddress.Port, _servePath).Uri;
    }

    /// <inheritdoc />
    protected override IHost CreateHost(IHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (Container.State != TestcontainersStates.Running)
        {
            throw new InvalidOperationException("The Inngest container must be running before the app starts, because the app's settings hold its address.");
        }

        // After the app's own options and those of ConfigureWebHost, so the container wins.
        builder.ConfigureServices(services => services.PostConfigure<InngestOptions>(Container.Configure));
        return base.CreateHost(builder);
    }
}
