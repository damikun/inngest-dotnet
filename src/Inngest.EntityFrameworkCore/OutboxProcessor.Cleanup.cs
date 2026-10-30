// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.EntityFrameworkCore;

internal sealed partial class OutboxProcessor<TContext>
{
    // Deletes the events Inngest accepted longer ago than the retention time, a batch at a time so that a large backlog
    // never holds locks for long. Returns how many it deleted.
    public async Task<int> CleanUpAsync(CancellationToken cancellationToken)
    {
        var settings = Settings;
        var cutoff = time.GetUtcNow().UtcDateTime - settings.Retention;
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var messages = scope.ServiceProvider.GetRequiredService<TContext>().Set<InngestOutboxMessage>();
            var total = 0;
            int deleted;
            do
            {
                deleted = await messages
                    .Where(message => message.SentAt != null && message.SentAt <= cutoff)
                    .OrderBy(message => message.SentAt)
                    .Take(settings.CleanupBatchSize)
                    .ExecuteDeleteAsync(cancellationToken)
                    .ConfigureAwait(false);
                total += deleted;
            }
            while (deleted >= settings.CleanupBatchSize);

            return total;
        }
    }
}
