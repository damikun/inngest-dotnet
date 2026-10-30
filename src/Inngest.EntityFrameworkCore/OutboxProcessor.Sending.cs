// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Inngest.EntityFrameworkCore;

internal sealed partial class OutboxProcessor<TContext>
{
    private const int MaxErrorLength = 2000;

    // Sends the claimed events and records, for each, whether Inngest took it. The outcome is saved however the sending ends,
    // the host stopping included, so that an event Inngest accepted is not sent again for no reason.
    private async Task SendAsync(TContext database, InngestOutboxOptions settings, Claim claim, CancellationToken cancellationToken)
    {
        var outcome = new Outcome(claim.Rows);
        try
        {
            await DeliverAsync(settings, claim, outcome, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await SaveAsync(database, settings, claim, outcome).ConfigureAwait(false);
        }
    }

    private async Task DeliverAsync(InngestOutboxOptions settings, Claim claim, Outcome outcome, CancellationToken cancellationToken)
    {
        var failure = await TrySendAsync(claim.Rows, settings, cancellationToken).ConfigureAwait(false);
        if (failure is null)
        {
            outcome.Succeeded(claim.Rows);
            return;
        }

        // Inngest turns a whole request down for one event it cannot take, a payload that is too large for instance. With the
        // others in the same request, that event would keep them from ever being sent, so each gets a request of its own.
        // Only a refusal of the events is worth that: if Inngest is down or the event key is wrong, a request for each
        // would multiply the traffic for nothing.
        if (claim.Rows.Count > 1 && IsRefusal(failure))
        {
            Log.Isolating(logger, claim.Rows.Count, _context, Describe(failure));
            foreach (var row in claim.Rows)
            {
                // A request may take until the send timeout, so one that the lease no longer covers is not started. The
                // rest of the events are given back and the next pass takes them.
                if (time.GetUtcNow().UtcDateTime + settings.SendTimeout >= claim.LeaseExpiresAt)
                {
                    break;
                }

                var single = await TrySendAsync([row], settings, cancellationToken).ConfigureAwait(false);
                if (single is null)
                {
                    outcome.Succeeded([row]);
                }
                else
                {
                    outcome.Failed(row, single);
                }
            }

            return;
        }

        foreach (var row in claim.Rows)
        {
            outcome.Failed(row, failure);
        }
    }

    // Returns why the send failed, or null. Only the host stopping ends in an exception.
    private async Task<Exception?> TrySendAsync(IReadOnlyList<InngestOutboxMessage> rows, InngestOutboxOptions settings, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(settings.SendTimeout, time);
        using var either = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var events = new InngestEvent[rows.Count];
            for (var i = 0; i < events.Length; i++)
            {
                events[i] = ToEvent(rows[i]);
            }

            await client.SendAsync(events, either.Token).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            return new TimeoutException($"Inngest did not take the events within {settings.SendTimeout}.", exception);
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    // The row's JSON is written to the request as it is. The id is the row's, on every attempt, which is what makes a resend harmless.
    private static InngestEvent ToEvent(InngestOutboxMessage row) =>
        new(row.Name, ReadObject(row.Data))
        {
            Id = row.Id,
            User = row.User is null ? null : ReadObject(row.User),
            Timestamp = new DateTimeOffset(DateTime.SpecifyKind(row.Timestamp, DateTimeKind.Utc)),
            Version = row.Version,
            Meta = row.Sessions is null ? null : new EventMeta { Sessions = ReadSessions(row.Sessions) },
        };

    private static JsonElement ReadObject(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 256 });
        return document.RootElement.Clone();
    }

    private static Dictionary<string, string?> ReadSessions(string json)
    {
        using var document = JsonDocument.Parse(json);
        var sessions = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var session in document.RootElement.EnumerateObject())
        {
            sessions[session.Name] = session.Value.ValueKind == JsonValueKind.String ? session.Value.GetString() : null;
        }

        return sessions;
    }

    // Failures that say Inngest will not take these events however often they are sent, as opposed to Inngest or the
    // network being unavailable for a while: the status codes of a payload it cannot process, and events the client
    // refused to write.
    private static bool IsRefusal(Exception exception) =>
        exception is ArgumentException or JsonException or InngestException { StatusCode: 400 or 409 or 412 or 413 or 422 };

    private static string Describe(Exception exception) =>
        exception is InngestException or TimeoutException ? exception.Message : $"{exception.GetType().Name}: {exception.Message}";

    // RetryDelay after the first failure, then twice as long after each further one, up to MaxRetryDelay. The exponent
    // stops growing long before the result could overflow.
    internal static TimeSpan RetryDelayFor(InngestOutboxOptions settings, int attempts)
    {
        var seconds = settings.RetryDelay.TotalSeconds * Math.Pow(2, Math.Min(attempts - 1, 30));
        return TimeSpan.FromSeconds(Math.Min(seconds, settings.MaxRetryDelay.TotalSeconds));
    }

    private async Task SaveAsync(TContext database, InngestOutboxOptions settings, Claim claim, Outcome outcome)
    {
        using var grace = new CancellationTokenSource(GraceTime, time);
        var cancellationToken = grace.Token;
        var now = time.GetUtcNow().UtcDateTime;
        var messages = database.Set<InngestOutboxMessage>();
        var token = claim.Token;

        // Every update names the token, so it only touches rows this dispatcher still holds. If the lease ran out and
        // another dispatcher took a row, it is that one's now, and a repeat of its send is dropped by Inngest.
        if (outcome.Sent.Count > 0)
        {
            var ids = outcome.Sent.Select(row => row.Id).ToList();
            await messages
                .Where(message => ids.Contains(message.Id) && message.LeaseToken == token)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(message => message.SentAt, (DateTime?)now)
                        .SetProperty(message => message.Attempts, message => message.Attempts + 1)
                        .SetProperty(message => message.LastError, (string?)null)
                        .SetProperty(message => message.LeaseToken, (string?)null)
                        .SetProperty(message => message.LeaseExpiresAt, (DateTime?)null),
                    cancellationToken)
                .ConfigureAwait(false);
            Log.Sent(logger, ids.Count, _context);
        }

        // Events that failed the same way after the same number of attempts wait the same time, so they share a statement.
        foreach (var group in outcome.Failures.GroupBy(failure => (failure.Row.Attempts, Reason: Describe(failure.Failure))))
        {
            var attempts = group.Key.Attempts + 1;
            var retryAt = now + RetryDelayFor(settings, attempts);
            var reason = Truncate(group.Key.Reason);
            var ids = group.Select(failure => failure.Row.Id).ToList();
            await messages
                .Where(message => ids.Contains(message.Id) && message.LeaseToken == token)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(message => message.Attempts, attempts)
                        .SetProperty(message => message.NextAttemptAt, retryAt)
                        .SetProperty(message => message.LastError, (string?)reason)
                        .SetProperty(message => message.LeaseToken, (string?)null)
                        .SetProperty(message => message.LeaseExpiresAt, (DateTime?)null),
                    cancellationToken)
                .ConfigureAwait(false);

            // An error is logged for each event Inngest refuses, because it will not go through until someone looks at it.
            if (ids.Count == 1 && IsRefusal(group.First().Failure))
            {
                var row = group.First().Row;
                Log.Refused(logger, row.Id, row.Name, _context, reason, retryAt);
            }
            else
            {
                Log.SendFailed(logger, ids.Count, _context, reason, retryAt);
            }
        }

        // Events nobody decided about go back without costing an attempt.
        var undecided = outcome.Undecided().Select(row => row.Id).ToList();
        if (undecided.Count > 0)
        {
            await messages
                .Where(message => undecided.Contains(message.Id) && message.LeaseToken == token)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(message => message.LeaseToken, (string?)null)
                        .SetProperty(message => message.LeaseExpiresAt, (DateTime?)null),
                    cancellationToken)
                .ConfigureAwait(false);
            Log.Released(logger, undecided.Count, _context);
        }
    }

    private static string Truncate(string text) => text.Length <= MaxErrorLength ? text : text[..MaxErrorLength];

    // Which of the claimed events were decided, and how.
    private sealed class Outcome(IReadOnlyList<InngestOutboxMessage> claimed)
    {
        private readonly HashSet<string> _decided = new(StringComparer.Ordinal);

        public List<InngestOutboxMessage> Sent { get; } = [];

        public List<(InngestOutboxMessage Row, Exception Failure)> Failures { get; } = [];

        public void Succeeded(IEnumerable<InngestOutboxMessage> rows)
        {
            foreach (var row in rows)
            {
                _decided.Add(row.Id);
                Sent.Add(row);
            }
        }

        public void Failed(InngestOutboxMessage row, Exception failure)
        {
            _decided.Add(row.Id);
            Failures.Add((row, failure));
        }

        public IEnumerable<InngestOutboxMessage> Undecided() => claimed.Where(row => !_decided.Contains(row.Id));
    }
}
