using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;

namespace Inngest.Tests.SignalR;

// The caller of a hub method, whose connection a test ends the way a client does (Disconnect) or the way the bridge does
// (Abort).
internal sealed class FakeCaller(string connectionId, ClaimsPrincipal? user = null) : HubCallerContext
{
    private readonly CancellationTokenSource _aborted = new();

    public override string ConnectionId { get; } = connectionId;

    public override string? UserIdentifier => user?.Identity?.Name;

    public override ClaimsPrincipal? User => user;

    public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

    public override IFeatureCollection Features { get; } = new FeatureCollection();

    public override CancellationToken ConnectionAborted => _aborted.Token;

    // Whether the bridge ended the connection, as it does for a client that cannot keep up.
    public bool Aborted { get; private set; }

    public override void Abort()
    {
        Aborted = true;
        _aborted.Cancel();
    }

    // The client goes away.
    public void Disconnect() => _aborted.Cancel();
}

internal sealed record Delivery(string ConnectionId, string Method, object?[] Args)
{
    // The text of the message, as the bridge sends it: the first argument.
    public string Text => (string)Args[0]!;
}

// Takes the place of the hub's lifetime manager, where every connection is reachable by id as it is on a backplane or
// in Azure SignalR Service, whichever instance sends. It records what is sent to a connection, can hold a send up as
// a slow client does, and records every other kind of send and every group call, which the bridge must never make.
internal sealed class RecordingLifetimeManager<THub> : HubLifetimeManager<THub>
    where THub : Hub
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<Delivery>> _deliveries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource> _held = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Exception> _failing = new(StringComparer.Ordinal);
    private readonly List<string> _otherCalls = [];

    // Everything but a send to a connection: the bridge sends by connection id, never to a group or to everyone.
    public IReadOnlyList<string> OtherCalls
    {
        get
        {
            lock (_gate)
            {
                return [.. _otherCalls];
            }
        }
    }

    public IReadOnlyList<Delivery> To(string connectionId)
    {
        lock (_gate)
        {
            return _deliveries.TryGetValue(connectionId, out var sent) ? [.. sent] : [];
        }
    }

    // The texts of the messages the connection received, in order.
    public IReadOnlyList<string> Messages(string connectionId, string method = "Realtime") =>
        [.. To(connectionId).Where(delivery => delivery.Method == method).Select(delivery => delivery.Text)];

    // Sends to the connection start but do not finish until Release, or until their token is cancelled.
    public void Hold(string connectionId)
    {
        lock (_gate)
        {
            _held[connectionId] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Release(string connectionId)
    {
        TaskCompletionSource? held;
        lock (_gate)
        {
            _held.Remove(connectionId, out held);
        }

        held?.TrySetResult();
    }

    public void FailSendsTo(string connectionId, Exception exception)
    {
        lock (_gate)
        {
            _failing[connectionId] = exception;
        }
    }

    public override async Task SendConnectionAsync(string connectionId, string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource? held;
        lock (_gate)
        {
            if (_failing.TryGetValue(connectionId, out var failure))
            {
                throw failure;
            }

            _held.TryGetValue(connectionId, out held);
            if (held is null)
            {
                Add(new Delivery(connectionId, methodName, args));
            }
        }

        if (held is not null)
        {
            await held.Task.WaitAsync(cancellationToken);
            lock (_gate)
            {
                Add(new Delivery(connectionId, methodName, args));
            }
        }
    }

    public override Task SendGroupAsync(string groupName, string methodName, object?[] args, CancellationToken cancellationToken = default) =>
        Record($"SendGroup {groupName}");

    public override Task SendGroupsAsync(IReadOnlyList<string> groupNames, string methodName, object?[] args, CancellationToken cancellationToken = default) =>
        Record("SendGroups");

    public override Task SendGroupExceptAsync(string groupName, string methodName, object?[] args, IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = default) =>
        Record($"SendGroupExcept {groupName}");

    public override Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
        Record($"AddToGroup {groupName}");

    public override Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
        Record($"RemoveFromGroup {groupName}");

    public override Task SendAllAsync(string methodName, object?[] args, CancellationToken cancellationToken = default) =>
        Record("SendAll");

    public override Task SendAllExceptAsync(string methodName, object?[] args, IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = default) =>
        Record("SendAllExcept");

    public override Task SendConnectionsAsync(IReadOnlyList<string> connectionIds, string methodName, object?[] args, CancellationToken cancellationToken = default) =>
        Record("SendConnections");

    public override Task SendUserAsync(string userId, string methodName, object?[] args, CancellationToken cancellationToken = default) =>
        Record("SendUser");

    public override Task SendUsersAsync(IReadOnlyList<string> userIds, string methodName, object?[] args, CancellationToken cancellationToken = default) =>
        Record("SendUsers");

    public override Task OnConnectedAsync(HubConnectionContext connection) => Task.CompletedTask;

    public override Task OnDisconnectedAsync(HubConnectionContext connection) => Task.CompletedTask;

    // Called under the lock.
    private void Add(Delivery delivery)
    {
        if (!_deliveries.TryGetValue(delivery.ConnectionId, out var sent))
        {
            _deliveries[delivery.ConnectionId] = sent = [];
        }

        sent.Add(delivery);
    }

    private Task Record(string call)
    {
        lock (_gate)
        {
            _otherCalls.Add(call);
        }

        return Task.CompletedTask;
    }
}
