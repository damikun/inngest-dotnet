// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Inngest.EntityFrameworkCore;

// Runs the processor for one DbContext for as long as the host runs: a pass whenever the table may hold something due,
// which is after a save that added events, after the poll interval and right away while there is more to do. Deleting
// sent events is a loop of its own, so that a long cleanup never delays a send.
internal sealed class OutboxDispatcher<TContext>(
    OutboxProcessor<TContext> processor,
    OutboxSignals signals,
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<OutboxDispatcher<TContext>> logger) : BackgroundService
    where TContext : DbContext
{
    private readonly string _context = typeof(TContext).Name;

    // A setup that cannot work stops the host from starting, instead of showing up as the same error on every poll.
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (processor.Settings.DispatcherEnabled)
        {
            EntityFrameworkLine.EnsureSupported();

            var scope = scopes.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                if (scope.ServiceProvider.GetRequiredService<TContext>().Model.FindEntityType(typeof(InngestOutboxMessage)) is null)
                {
                    throw new InvalidOperationException(
                        $"{typeof(TContext).Name} does not map the outbox table. Call modelBuilder.AddInngestOutbox() in OnModelCreating.");
                }
            }
        }

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = processor.Settings;
        if (!settings.DispatcherEnabled)
        {
            Log.Disabled(logger, _context);
            return;
        }

        Log.Started(logger, _context, settings.PollInterval);
        var cleanup = CleanUpLoopAsync(stoppingToken);
        var signal = signals.For(typeof(TContext));
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                // Events that were claimed are not necessarily all there is, so a pass that found some is followed by another.
                if (await processor.DispatchAsync(stoppingToken).ConfigureAwait(false) > 0)
                {
                    failures = 0;
                    continue;
                }

                failures = 0;
                wait = processor.Settings.PollInterval;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // The database being down or not migrated yet must not take the host down, and the events are safe in it.
                failures++;
                wait = RetryDelayAfterFailures(failures);
                Log.PassFailed(logger, exception, _context, wait);
            }

            try
            {
                await signal.WaitAsync(wait, time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await cleanup.ConfigureAwait(false);
    }

    // Like the delay between attempts to send an event: it doubles with every pass in a row that failed.
    private TimeSpan RetryDelayAfterFailures(int failures) => OutboxProcessor<TContext>.RetryDelayFor(processor.Settings, failures);

    private async Task CleanUpLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // The first cleanup comes after one interval, when startup has settled.
                await Task.Delay(processor.Settings.CleanupInterval, time, stoppingToken).ConfigureAwait(false);
                var deleted = await processor.CleanUpAsync(stoppingToken).ConfigureAwait(false);
                if (deleted > 0)
                {
                    Log.CleanedUp(logger, deleted, _context, processor.Settings.Retention);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                Log.CleanupFailed(logger, exception, _context);
            }
        }
    }
}
