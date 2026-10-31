// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inngest.EntityFrameworkCore;

// The work on the table, one pass at a time: claim the events that are due, send them in one request and record how that
// went. The dispatcher calls it in a loop and tests call it directly.
//
// Dispatchers in any number of processes share the table and take no lock on it. A claim is an UPDATE that writes a token
// of its own and an expiry on the due rows that are not leased, and the dispatcher then reads the rows that hold its token:
// those are its events, and nobody else's. It takes two statements. Choosing the rows and updating them in one, with the
// test for "due" inside a subquery, would not do: when the UPDATE has to wait for a row that another one is updating,
// PostgreSQL does not run the subquery again once the row is free, so the second UPDATE takes the row as well and
// overwrites the token of the first. In the WHERE clause of the UPDATE itself the test is made again on the current version
// of the row, and the claim of the loser changes nothing. A dispatcher that dies keeps its rows until the lease expires;
// Inngest drops the repeat by event id.
internal sealed partial class OutboxProcessor<TContext>(
    IServiceScopeFactory scopes,
    IInngestClient client,
    IOptionsMonitor<InngestOutboxOptions> options,
    TimeProvider time,
    ILogger<OutboxProcessor<TContext>> logger)
    where TContext : DbContext
{
    // How long the outcome of a pass may take to be saved when the host is already stopping.
    private static readonly TimeSpan GraceTime = TimeSpan.FromSeconds(10);

    private readonly string _context = typeof(TContext).Name;

    public InngestOutboxOptions Settings => options.Get(OutboxNames.For<TContext>());

    // Claims what is due, sends it and records the outcome. Returns how many events it took, so that the caller looks
    // again at once when there were some and waits when there were none.
    public async Task<int> DispatchAsync(CancellationToken cancellationToken)
    {
        var settings = Settings;
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var database = scope.ServiceProvider.GetRequiredService<TContext>();
            if (await ClaimAsync(database, settings, cancellationToken).ConfigureAwait(false) is not { } claim)
            {
                return 0;
            }

            await SendAsync(database, settings, claim, cancellationToken).ConfigureAwait(false);
            return claim.Rows.Count;
        }
    }

    // What a dispatcher took: its token, the rows holding it and when the lease on them ends.
    private sealed record Claim(string Token, IReadOnlyList<InngestOutboxMessage> Rows, DateTime LeaseExpiresAt);

    private async Task<Claim?> ClaimAsync(TContext database, InngestOutboxOptions settings, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var messages = database.Set<InngestOutboxMessage>();

        var candidates = await messages
            .AsNoTracking()
            .Where(IsDue(now))
            .OrderBy(message => message.NextAttemptAt)
            .ThenBy(message => message.Id)
            .Select(message => message.Id)
            .Take(settings.BatchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return null;
        }

        var token = Guid.NewGuid().ToString("N");
        var leaseExpiresAt = now + settings.LeaseDuration;
        var claimed = await messages
            .Where(message => candidates.Contains(message.Id))
            .Where(IsDue(now))
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(message => message.LeaseToken, token)
                    .SetProperty(message => message.LeaseExpiresAt, (DateTime?)leaseExpiresAt),
                cancellationToken)
            .ConfigureAwait(false);

        // Other dispatchers took all of them first.
        if (claimed == 0)
        {
            return null;
        }

        // The token has no index, and the table holds a day of sent rows, so the read is bound to the rows chosen above, which
        // are found by key. Whatever it returns is a subset of them: the rows other dispatchers took have another token.
        var rows = await messages
            .AsNoTracking()
            .Where(message => candidates.Contains(message.Id) && message.LeaseToken == token)
            .OrderBy(message => message.NextAttemptAt)
            .ThenBy(message => message.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return new Claim(token, rows, leaseExpiresAt);
    }

    // Not sent, not waiting for a retry and not held by a dispatcher whose lease is still running.
    private static Expression<Func<InngestOutboxMessage, bool>> IsDue(DateTime now) =>
        message => message.SentAt == null
            && message.NextAttemptAt <= now
            && (message.LeaseExpiresAt == null || message.LeaseExpiresAt <= now);
}
