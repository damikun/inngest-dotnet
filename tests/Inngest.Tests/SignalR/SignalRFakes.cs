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

    // What the news of a lost subscription says: the channel and topics the client asked for, and the reason.
    public (string Channel, string[] Topics, string Reason) Lost => ((string)Args[0]!, (string[])Args[1]!, (string)Args[2]!);
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
    private readonly HashSet<string> _waiting = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Exception Exception, int Remaining)> _failing = new(StringComparer.Ordinal);
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

    // Whether a send to the connection has started and is held up.
    public bool IsHeld(string connectionId)
    {
        lock (_gate)
        {
            return _waiting.Contains(connectionId);
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

    // The next sends to the connection throw, this many of them.
    public void FailSendsTo(string connectionId, Exception exception, int times = int.MaxValue)
    {
        lock (_gate)
        {
            _failing[connectionId] = (exception, times);
        }
    }

    public override async Task SendConnectionAsync(string connectionId, string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource? held;
        lock (_gate)
        {
            if (_failing.TryGetValue(connectionId, out var failure) && failure.Remaining > 0)
            {
                _failing[connectionId] = (failure.Exception, failure.Remaining - 1);
                throw failure.Exception;
            }

            _held.TryGetValue(connectionId, out held);
            if (held is null)
            {
                Add(new Delivery(connectionId, methodName, args));
            }
            else
            {
                _waiting.Add(connectionId);
            }
        }

        if (held is not null)
        {
            try
            {
                await held.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                lock (_gate)
                {
                    _waiting.Remove(connectionId);
                }
            }

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

// The lifetime manager of a real hub, with the sends to one connection held up as the writes to a client that does not
// read are held up. Everything else is the manager it wraps.
internal sealed class HoldingLifetimeManager<THub>(HubLifetimeManager<THub> inner) : HubLifetimeManager<THub>
    where THub : Hub
{
    private readonly Lock _gate = new();
    private string? _connectionId;
    private TaskCompletionSource? _release;
    private bool _waiting;

    // Whether a send to the connection has started and is held up.
    public bool IsHeld
    {
        get
        {
            lock (_gate)
            {
                return _waiting;
            }
        }
    }

    public void Hold(string connectionId)
    {
        lock (_gate)
        {
            _connectionId = connectionId;
            _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Release()
    {
        TaskCompletionSource? release;
        lock (_gate)
        {
            release = _release;
            _release = null;
            _connectionId = null;
        }

        release?.TrySetResult();
    }

    public override async Task SendConnectionAsync(string connectionId, string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource? release;
        lock (_gate)
        {
            release = connectionId == _connectionId ? _release : null;
            _waiting |= release is not null;
        }

        if (release is not null)
        {
            try
            {
                await release.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                lock (_gate)
                {
                    _waiting = false;
                }
            }
        }

        await inner.SendConnectionAsync(connectionId, methodName, args, cancellationToken);
    }

    public override Task OnConnectedAsync(HubConnectionContext connection) => inner.OnConnectedAsync(connection);

    public override Task OnDisconnectedAsync(HubConnectionContext connection) => inner.OnDisconnectedAsync(connection);

    public override Task SendAllAsync(string methodName, object?[] args, CancellationToken cancellationToken = default) =>
        inner.SendAllAsync(methodName, args, cancellationToken);

    public override Task SendAllExceptAsync(string methodName, object?[] args, IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = default) =>
        inner.SendAllExceptAsync(methodName, args, excludedConnectionIds, cancellationToken);

    public override Task SendConnectionsAsync(IReadOnlyList<string> connectionIds, string methodName, object?[] args, CancellationToken cancellationToken = default) =>
        inner.SendConnectionsAsync(connectionIds, methodName, args, cancellationToken);

    public override Task SendGroupAsync(string groupName, string methodName, object?[] args, CancellationToken cancellationToken = default) =>
        inner.SendGroupAsync(groupName, methodName, args, cancellationToken);

    public override Task SendGroupsAsync(IReadOnlyList<string> groupNames, string methodName, object?[] args, CancellationToken cancellationToken = default) =>
        inner.SendGroupsAsync(groupNames, methodName, args, cancellationToken);

    public override Task SendGroupExceptAsync(string groupName, string methodName, object?[] args, IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = default) =>
        inner.SendGroupExceptAsync(groupName, methodName, args, excludedConnectionIds, cancellationToken);

    public override Task SendUserAsync(string userId, string methodName, object?[] args, CancellationToken cancellationToken = default) =>
        inner.SendUserAsync(userId, methodName, args, cancellationToken);

    public override Task SendUsersAsync(IReadOnlyList<string> userIds, string methodName, object?[] args, CancellationToken cancellationToken = default) =>
        inner.SendUsersAsync(userIds, methodName, args, cancellationToken);

    public override Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
        inner.AddToGroupAsync(connectionId, groupName, cancellationToken);

    public override Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
        inner.RemoveFromGroupAsync(connectionId, groupName, cancellationToken);
}
