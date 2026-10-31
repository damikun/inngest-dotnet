// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Text.Json;
using Inngest.Configuration;
using Inngest.Execution;
using Inngest.Registration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inngest.UserContext;

// Both directions of the user context. When a function starts it restores the user from the triggering event for the
// run's context. When events are sent it attaches the user: the one the source names when the send is outside a run, or
// the run's own when it is inside one. Like all middleware it is scoped to one call.
internal sealed class UserContextMiddleware(
    UserContextCodec codec,
    IOptions<UserContextOptions> options,
    InngestSettings settings,
    IFunctionRegistry registry,
    TimeProvider time,
    ILogger<UserContextMiddleware> logger,
    IUserContextSource? source = null) : InngestMiddleware
{
    // The first and the last millisecond a DateTimeOffset can hold.
    private const long MinMilliseconds = -62135596800000;
    private const long MaxMilliseconds = 253402300799999;

    private readonly UserContextOptions _options = options.Value;

    // Runs on every call of the run, replays included. The user is judged by when the run was created and when its event
    // asks it to start, both fixed for the run, so every call gives the same answer and a function that checks the user
    // at its top behaves the same on each replay.
    public override Task<object?> WrapFunctionAsync(FunctionContext context, Func<Task<object?>> next)
    {
        Restore(context);
        return next();
    }

    public override async Task<SendEventsResult> WrapSendEventAsync(
        FunctionContext? context,
        IReadOnlyList<InngestEvent> events,
        Func<IReadOnlyList<InngestEvent>, Task<SendEventsResult>> next,
        CancellationToken cancellationToken)
    {
        var snapshot = await FindSnapshotAsync(context, cancellationToken).ConfigureAwait(false);
        return await next(snapshot is null ? events : Attach(events, snapshot)).ConfigureAwait(false);
    }

    private void Restore(FunctionContext context)
    {
        if (!TryFindUser(context, out var user) || !user.Json.TryGetProperty(UserContextCodec.Key, out var envelope))
        {
            RunUsers.Set(context, null);
            return;
        }

        // A run's id says when Inngest created it. The run may start later than that, at the timestamp of its event, which
        // whoever sends the event chooses and can put years ahead, so a user is judged by the later of the two. A run
        // delayed by a queue was created in time and keeps its user.
        var read = codec.Read(envelope, user.EventName, Later(RunIds.StartOf(user.RunId) ?? time.GetUtcNow(), user.Scheduled), user.Function);
        RunUsers.Set(context, read.Snapshot);
        if (read.Snapshot is null)
        {
            Reject(context, user.EventName, read.Problem, user.OfFailedRun);
        }
    }

    // The event whose user the run gets: the triggering event, or for a failure handler the event of the failed run,
    // which inngest/function.failed carries. A function that batches events has no single user, so it gets none.
    private bool TryFindUser(FunctionContext context, out EventUser found)
    {
        found = default;
        var raw = context.RawEvent;
        if (raw.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var @event = raw;
        var runId = context.RunId;
        var runningFunction = Compose(context.FunctionId);
        var ofFailedRun = false;
        if (registry.TryGet(runningFunction, out var function))
        {
            if (function.FailureHandlerFor is { } failedId)
            {
                if (context.EventName != SystemEvents.FunctionFailed ||
                    !raw.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Object ||
                    !data.TryGetProperty("event", out @event) ||
                    @event.ValueKind != JsonValueKind.Object ||
                    IsBatched(failedId))
                {
                    return false;
                }

                // The user is judged as the failed run judged it, not by the time its failure was found.
                ofFailedRun = true;
                runningFunction = Compose(failedId);
                if (data.TryGetProperty("run_id", out var failedRun) && failedRun.ValueKind == JsonValueKind.String)
                {
                    runId = failedRun.GetString()!;
                }
            }
            else if (function.Options.BatchEvents is not null)
            {
                return false;
            }
        }

        if (!@event.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var name = @event.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString()!
            : string.Empty;
        // An invocation is for one function, and the text must say that it is this one.
        found = new EventUser(user, name, runId, ScheduledAt(@event), name == SystemEvents.FunctionInvoked ? runningFunction : null, ofFailedRun);
        return true;
    }

    // When the event asks its run to start. Inngest keeps the ts of the event as it was sent, so the value is the same on
    // every call of the run.
    private static DateTimeOffset? ScheduledAt(JsonElement @event) =>
        @event.TryGetProperty("ts", out var ts) &&
        ts.ValueKind == JsonValueKind.Number &&
        ts.TryGetInt64(out var milliseconds) &&
        milliseconds is >= MinMilliseconds and <= MaxMilliseconds
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            : null;

    private static DateTimeOffset Later(DateTimeOffset created, DateTimeOffset? scheduled) =>
        scheduled is { } at && at > created ? at : created;

    private bool IsBatched(string functionId) =>
        registry.TryGet(Compose(functionId), out var function) && function.Options.BatchEvents is not null;

    // The registry knows functions by "<app id>-<function id>".
    private string Compose(string functionId) => $"{settings.AppId}-{functionId}";

    // A failure handler runs because the run it belongs to failed, often on this very user, so it never fails on one: it
    // takes the anonymous path, with the warning, and can do what it is there for.
    private void Reject(FunctionContext context, string eventName, UserReadProblem problem, bool ofFailedRun)
    {
        if (_options.OnInvalid == InvalidUserContextAction.FailRun && !ofFailedRun)
        {
            var message = $"The user in event {eventName} cannot be used because {Describe(problem)}.";
            throw problem == UserReadProblem.Unreadable ? new InvalidOperationException(message) : new NonRetriableException(message);
        }

        Log.UserNotUsed(context.Logger, eventName, Describe(problem));
    }

    private static string Describe(UserReadProblem problem) => problem switch
    {
        UserReadProblem.Expired => "it has expired",
        UserReadProblem.WrongEvent => "it was made for another event or function",
        UserReadProblem.Unsupported => "its format is not known to this version",
        UserReadProblem.Unreadable => "it could not be unprotected: it was changed, or made with a key or purpose this app does not have",
        _ => "it is not valid",
    };

    // Inside a run, events carry the run's user. The HTTP request that called the app then belongs to Inngest, so the
    // source is never asked.
    private async ValueTask<UserSnapshot?> FindSnapshotAsync(FunctionContext? context, CancellationToken cancellationToken)
    {
        var run = context ?? Invocation.AmbientContext;
        if (run is not null)
        {
            return _options.Propagate ? RunUsers.Of(run).Snapshot : null;
        }

        return source is null ? null : codec.Capture(await source.GetUserAsync(cancellationToken).ConfigureAwait(false));
    }

    // Each event gets its own protected text, because the text is tied to the event's name; events of one name share it. An
    // event whose run would start after the user expired gets none: a run that sleeps past the expiry would otherwise
    // send on a payload that is dead on arrival.
    private IReadOnlyList<InngestEvent> Attach(IReadOnlyList<InngestEvent> events, UserSnapshot snapshot)
    {
        InngestEvent[]? attached = null;
        Dictionary<string, string?>? texts = null;
        var now = time.GetUtcNow();
        var told = false;
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i] is not { Name: { Length: > 0 } name } @event || _options.EventFilter?.Invoke(@event) == false)
            {
                continue;
            }

            if (snapshot.ExpiresBefore(@event.Timestamp is { } scheduled && scheduled > now ? scheduled : now))
            {
                if (!told)
                {
                    Log.UserExpiresBeforeEvent(logger, name, snapshot.ExpiresAt);
                    told = true;
                }

                continue;
            }

            texts ??= new Dictionary<string, string?>(StringComparer.Ordinal);
            if (!texts.TryGetValue(name, out var protectedText))
            {
                texts[name] = protectedText = codec.Protect(snapshot, name);
                if (protectedText is null)
                {
                    Log.UserTooLarge(logger, name, _options.MaxSize);
                }
            }

            if (protectedText is not null && EventUsers.WithEnvelope(@event.User, protectedText, settings.SerializerOptions) is { } user)
            {
                attached ??= [.. events];
                attached[i] = @event with { User = user };
            }
        }

        return attached ?? events;
    }

    private readonly record struct EventUser(
        JsonElement Json,
        string EventName,
        string RunId,
        DateTimeOffset? Scheduled,
        string? Function,
        bool OfFailedRun);
}
