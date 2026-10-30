// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Text.Json;
using Inngest.Blazor;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Inngest;

/// <summary>
/// Shows the realtime messages of a topic, or of several topics of a channel, as they are published: it subscribes
/// through the IRealtimeSource of the app, updates itself as messages arrive and hands its RealtimeState to
/// ChildContent. It draws nothing of its own. It subscribes after its first interactive render, never while
/// prerendering, so a page may be prerendered or run on the server, in WebAssembly or in interactive Auto without a
/// change. It subscribes again when Topic, or Channel and Topics, change, and unsubscribes when it is disposed.
/// </summary>
/// <typeparam name="TData">
/// The type of the data of the messages. Topic reads it as a TData; Channel with Topics gives the data as JsonElement.
/// </typeparam>
public sealed class RealtimeView<TData> : ComponentBase, IAsyncDisposable
{
    // Disposing waits this long for the subscription to end, so that a source that ignores cancellation cannot hold up
    // the end of a circuit.
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly RealtimeState<TData> _state = new();
    private Subscription? _wanted;
    private Subscription? _running;
    private CancellationTokenSource? _stop;
    private Task _loop = Task.CompletedTask;
    private int _generation;
    private bool _rendered;
    private bool _disposed;

    /// <summary>The typed topic to watch. Set this, or Channel with Topics. Leave both unset to watch nothing yet.</summary>
    [Parameter]
    public RealtimeTopic<TData>? Topic { get; set; }

    /// <summary>The channel to watch, with Topics. TData must be JsonElement.</summary>
    [Parameter]
    public string? Channel { get; set; }

    /// <summary>The topics of Channel to watch.</summary>
    [Parameter]
    public IReadOnlyList<string>? Topics { get; set; }

    /// <summary>
    /// How many of the newest messages RealtimeState.Messages keeps, 100 by default. The latest message of each topic
    /// and the text of streams are kept besides.
    /// </summary>
    [Parameter]
    public int MaxMessages { get; set; } = 100;

    /// <summary>
    /// Hands a failed subscription to the nearest ErrorBoundary, which is the circuit's end when there is none. By
    /// default the view stays on screen with Connection Failed and Error set.
    /// </summary>
    [Parameter]
    public bool ThrowOnError { get; set; }

    /// <summary>
    /// Called for each message, in order, after RealtimeState has taken it; the next message waits until it returns. An
    /// exception from it fails the subscription.
    /// </summary>
    [Parameter]
    public EventCallback<RealtimeMessage<TData>> OnMessage { get; set; }

    /// <summary>The content, given the RealtimeState, which is rendered again whenever it changes.</summary>
    [Parameter]
    public RenderFragment<RealtimeState<TData>>? ChildContent { get; set; }

    [Inject]
    private IRealtimeSource Source { get; set; } = default!;

    [Inject]
    private ILogger<RealtimeView<TData>> Logger { get; set; } = default!;

    [Inject]
    private IServiceProvider Services { get; set; } = default!;

    /// <summary>
    /// Subscribes again from now on, with the messages cleared. It tries again after a failed subscription, which
    /// only a change of parameters otherwise does. Call it from an event handler.
    /// </summary>
    public void Restart()
    {
        if (_disposed || !_rendered)
        {
            return;
        }

        Stop();
        _running = null;
        Reconcile();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        var time = Services.GetService<TimeProvider>() ?? TimeProvider.System;
        try
        {
            await _loop.WaitAsync(StopTimeout, time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log.RealtimeViewDidNotStop(Logger, _running?.Channel ?? string.Empty, StopTimeout.TotalSeconds);
        }
    }

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        if (MaxMessages < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxMessages), MaxMessages, "MaxMessages cannot be negative.");
        }

        _state.Capacity = MaxMessages;
        _wanted = Resolve();
        if (_rendered)
        {
            Reconcile();
        }
    }

    /// <inheritdoc />
    protected override void OnAfterRender(bool firstRender)
    {
        // Prerendering and static rendering never get here, which is what keeps them from subscribing.
        if (firstRender)
        {
            _rendered = true;
            Reconcile();
        }
    }

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (ChildContent is { } content)
        {
            builder.AddContent(0, content, _state);
        }
    }

    private Subscription? Resolve()
    {
        var hasChannel = Channel is not null || Topics is not null;
        if (Topic is not null)
        {
            return hasChannel
                ? throw new InvalidOperationException($"{nameof(RealtimeView<TData>)} watches a {nameof(Topic)}, or a {nameof(Channel)} with its {nameof(Topics)}, not both.")
                : new Subscription(Topic.Channel, [Topic.Name], Typed: true);
        }

        if (!hasChannel)
        {
            return null;
        }

        if (string.IsNullOrEmpty(Channel) || Topics is not { Count: > 0 })
        {
            throw new InvalidOperationException($"{nameof(RealtimeView<TData>)} needs both a {nameof(Channel)} and its {nameof(Topics)}.");
        }

        return typeof(TData) == typeof(JsonElement)
            ? new Subscription(Channel, [.. Topics], Typed: false)
            : throw new InvalidOperationException($"{nameof(Channel)} and {nameof(Topics)} give the data as {nameof(JsonElement)}, so TData must be {nameof(JsonElement)}; use {nameof(Topic)} for data of another type.");
    }

    // Makes the running subscription the one the parameters ask for.
    private void Reconcile()
    {
        if (_disposed || Subscription.Same(_wanted, _running))
        {
            return;
        }

        Stop();
        _running = _wanted;
        if (_running is null)
        {
            _state.Begin(RealtimeConnectionState.Idle);
        }
        else
        {
            _state.Begin(RealtimeConnectionState.Connecting);
            Start(_running);
        }

        StateHasChanged();
    }

    private void Start(Subscription subscription)
    {
        var generation = _generation;
        var stop = _stop = new CancellationTokenSource();
        _loop = RunAsync(_loop, generation, subscription, stop.Token);
    }

    // Ends the running subscription, and makes whatever it still delivers count for nothing.
    private void Stop()
    {
        _generation++;

        // Not disposed: it has no timer, and the loop may still be using its token while it ends.
        _stop?.Cancel();
        _stop = null;
    }

    // Reads the subscription until it ends. It never faults, and it touches the component only through InvokeAsync, which
    // runs on the renderer's thread and is where a stale generation is told apart from the current one.
    private async Task RunAsync(Task previous, int generation, Subscription subscription, CancellationToken cancellationToken)
    {
        try
        {
            // One connection at a time: the loop this one replaces has ended first.
            await previous.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var messages = Open(subscription, state => _ = InvokeAsync(() => Report(generation, state)), cancellationToken);
            await foreach (var message in messages.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                await InvokeAsync(() => ApplyAsync(generation, message)).ConfigureAwait(false);
            }

            await InvokeAsync(() => Report(generation, RealtimeConnectionState.Closed)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped.
        }
        catch (Exception exception)
        {
            await FailAsync(generation, subscription, exception).ConfigureAwait(false);
        }
    }

    private IAsyncEnumerable<RealtimeMessage<TData>> Open(Subscription subscription, Action<RealtimeConnectionState> report, CancellationToken cancellationToken) =>
        subscription.Typed
            ? Source.SubscribeAsync(new RealtimeTopic<TData>(subscription.Channel, subscription.Topics[0]), cancellationToken, report)

            // Resolve checked that TData is JsonElement, which this is then.
            : (IAsyncEnumerable<RealtimeMessage<TData>>)(object)Source.SubscribeAsync(subscription.Channel, subscription.Topics, cancellationToken, report);

    private bool IsCurrent(int generation) => !_disposed && generation == _generation;

    private void Report(int generation, RealtimeConnectionState connection)
    {
        if (IsCurrent(generation))
        {
            _state.Report(connection);
            StateHasChanged();
        }
    }

    private async Task ApplyAsync(int generation, RealtimeMessage<TData> message)
    {
        if (!IsCurrent(generation))
        {
            return;
        }

        _state.Add(message);
        StateHasChanged();
        if (OnMessage.HasDelegate)
        {
            await OnMessage.InvokeAsync(message).ConfigureAwait(false);
        }
    }

    private async Task FailAsync(int generation, Subscription subscription, Exception exception)
    {
        try
        {
            await InvokeAsync(async () =>
            {
                if (!IsCurrent(generation))
                {
                    return;
                }

                Log.RealtimeViewFailed(Logger, exception, subscription.Channel);
                _state.Fail(exception);
                StateHasChanged();
                if (ThrowOnError)
                {
                    await DispatchExceptionAsync(exception).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            // The page or the circuit went away while the failure was being delivered.
            Log.RealtimeViewFailed(Logger, failure, subscription.Channel);
        }
    }

    private sealed record Subscription(string Channel, string[] Topics, bool Typed)
    {
        public static bool Same(Subscription? left, Subscription? right) =>
            left is null || right is null
                ? left is null && right is null
                : left.Typed == right.Typed && left.Channel == right.Channel && left.Topics.AsSpan().SequenceEqual(right.Topics);
    }
}
