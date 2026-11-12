// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Globalization;
using Inngest.Rest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Inngest.Testing.Containers;

/// <summary>
/// Sends events to an Inngest server that a test started and follows the runs they start, with the SDK's own
/// <see cref="IInngestClient"/> and <see cref="IInngestRunsClient"/>. Against the dev server it sends no keys; against a
/// self-hosted server it sends events with its event key and reads runs with its signing key. Each read of the REST API
/// carries a query parameter of its own, so that the server's cache (3 s for a run, 15 s for the runs of an event) never
/// answers it, and waits read a tenth of a second apart at first and at most half a second apart. Get one from
/// <see cref="InngestServerContainer.CreateClient"/>, or create one for a server that runs elsewhere.
/// </summary>
public sealed class InngestServerClient : IDisposable, IAsyncDisposable
{
    /// <summary>The longest a <see cref="SendAndWaitAsync(InngestEvent, int, TimeSpan?, CancellationToken)"/> waits unless told otherwise.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    // The server is on the same machine, so reading often costs nothing, and a test should not wait for a slow poll.
    private static readonly RunPolling Polling = new(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));

    private readonly ServiceProvider _services;

    /// <summary>
    /// Initializes a new instance of the <see cref="InngestServerClient"/> class.
    /// </summary>
    /// <param name="baseAddress">The address of the server's API, such as <see cref="InngestServerContainer.GetBaseAddress"/>.</param>
    /// <param name="signingKey">The signing key of a self-hosted server, which its REST API wants; null for the dev server.</param>
    /// <param name="eventKey">The event key of a self-hosted server; null for the dev server, which takes any key.</param>
    /// <param name="timeProvider">The clock of the waits' delays and deadlines; the system's when null.</param>
    public InngestServerClient(Uri baseAddress, string? signingKey = null, string? eventKey = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        BaseAddress = baseAddress;

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton(timeProvider ?? TimeProvider.System);
        services.AddSingleton(Polling);
        services.AddInngest(options =>
        {
            options.AppId = "inngest-testing-containers";
            options.BaseUrl = baseAddress;
            if (signingKey is null)
            {
                options.IsDev = true;
                options.DevServerUrl = baseAddress;
            }
            else
            {
                options.IsDev = false;
                options.SigningKey = signingKey;
                options.EventKey = eventKey;
            }
        });
        services.ConfigureHttpClientDefaults(http => http.AddHttpMessageHandler(static () => new FreshReads()));
        _services = services.BuildServiceProvider();

        Events = _services.GetRequiredService<IInngestClient>();
        Runs = _services.GetRequiredService<IInngestRunsClient>();
    }

    /// <summary>Gets the address of the server's API.</summary>
    public Uri BaseAddress { get; }

    /// <summary>Gets the client that sends events to the server.</summary>
    public IInngestClient Events { get; }

    /// <summary>Gets the client that reads, waits for and cancels the server's runs.</summary>
    public IInngestRunsClient Runs { get; }

    /// <summary>
    /// Sends the event, waits until it has started <paramref name="expectedRuns"/> runs and they have all ended, and returns
    /// them with their status and output (<see cref="FunctionRun.GetOutput{T}"/>). An event starts one run for each
    /// function it triggers. When more runs are listed by then, they are all returned. For an event that may start no
    /// run, send it with <see cref="Events"/> and wait with <see cref="IInngestRunsClient.WaitForEventRunsToSettleAsync"/>.
    /// </summary>
    /// <param name="event">The event to send.</param>
    /// <param name="expectedRuns">How many runs the event starts, at least 1.</param>
    /// <param name="timeout">How long to wait for the runs; <see cref="DefaultTimeout"/> when null.</param>
    /// <param name="cancellationToken">Cancels the send and the wait.</param>
    /// <returns>The runs the event started, in the order the server listed them.</returns>
    /// <exception cref="TimeoutException">Fewer runs started, or not all of them ended, in time.</exception>
    public async Task<IReadOnlyList<FunctionRun>> SendAndWaitAsync(
        InngestEvent @event,
        int expectedRuns = 1,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRuns, 1);

        var sent = await Events.SendAsync(@event, cancellationToken).ConfigureAwait(false);
        return await Runs.WaitForEventRunsAsync(sent.Ids[0], expectedRuns, timeout ?? DefaultTimeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the event data, named by its type, and waits for the runs it starts, as
    /// <see cref="SendAndWaitAsync(InngestEvent, int, TimeSpan?, CancellationToken)"/> does.
    /// </summary>
    /// <typeparam name="TEvent">The type of the event data, which names the event.</typeparam>
    /// <param name="data">The data of the event.</param>
    /// <param name="expectedRuns">How many runs the event starts, at least 1.</param>
    /// <param name="timeout">How long to wait for the runs; <see cref="DefaultTimeout"/> when null.</param>
    /// <param name="cancellationToken">Cancels the send and the wait.</param>
    /// <returns>The runs the event started, in the order the server listed them.</returns>
    /// <exception cref="TimeoutException">Fewer runs started, or not all of them ended, in time.</exception>
    public Task<IReadOnlyList<FunctionRun>> SendAndWaitAsync<TEvent>(
        TEvent data,
        int expectedRuns = 1,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        where TEvent : IInngestEventData =>
        SendAndWaitAsync(InngestEvent.From(data), expectedRuns, timeout, cancellationToken);

    /// <inheritdoc />
    public void Dispose() => _services.Dispose();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _services.DisposeAsync();

    // Gives each GET of the v1 REST API a query parameter of its own. The server caches those GETs by their URL and
    // Authorization header, which would answer a poll with the same list of runs for 15 s, and ignores parameters it
    // does not know there. The v2 API caches nothing and refuses parameters it does not know, so it is left alone.
    private sealed class FreshReads : DelegatingHandler
    {
        private static long s_counter;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri is { } uri && uri.AbsolutePath.StartsWith("/v1/", StringComparison.Ordinal))
            {
                var number = Interlocked.Increment(ref s_counter).ToString(CultureInfo.InvariantCulture);
                var builder = new UriBuilder(uri) { Query = uri.Query.Length > 1 ? $"{uri.Query[1..]}&cb={number}" : $"cb={number}" };
                request.RequestUri = builder.Uri;
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
