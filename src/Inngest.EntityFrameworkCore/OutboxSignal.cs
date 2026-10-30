// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Threading.Channels;

namespace Inngest.EntityFrameworkCore;

// How a save that added events tells the dispatcher to look at the table now instead of at the next poll. A
// notification is remembered until the dispatcher waits again, so one that arrives while it is busy is not lost, and
// any number of them before that count as one.
internal sealed class OutboxSignal
{
    private readonly Channel<bool> _pending = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
    });

    public void Notify() => _pending.Writer.TryWrite(true);

    // Returns true when notified and false when the interval is over. Only the token stops it with an exception.
    public async Task<bool> WaitAsync(TimeSpan interval, TimeProvider time, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(interval, time);
        using var either = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await _pending.Reader.ReadAsync(either.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
