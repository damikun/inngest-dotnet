using System.Collections.Concurrent;

namespace Inngest.IntegrationTests.Outbox;

// Stands in for Inngest where a test is about the database: keeps what the dispatchers send.
internal sealed class RecordingClient : IInngestClient
{
    private readonly ConcurrentQueue<IReadOnlyList<InngestEvent>> _requests = new();

    public string AppId => "shop";

    public Func<IReadOnlyList<InngestEvent>, CancellationToken, Task>? OnSend { get; set; }

    public IReadOnlyList<IReadOnlyList<InngestEvent>> Requests => [.. _requests];

    public IReadOnlyList<InngestEvent> Events => [.. _requests.SelectMany(request => request)];

    public Task<SendEventsResult> SendAsync(InngestEvent @event, CancellationToken cancellationToken = default) =>
        SendAsync([@event], cancellationToken);

    public async Task<SendEventsResult> SendAsync(IEnumerable<InngestEvent> events, CancellationToken cancellationToken = default)
    {
        var batch = events.ToList();
        if (OnSend is { } onSend)
        {
            await onSend(batch, cancellationToken);
        }

        _requests.Enqueue(batch);
        return new SendEventsResult([.. batch.Select(@event => @event.Id ?? string.Empty)]);
    }
}
