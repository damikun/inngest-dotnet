// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Text.Json;
using Inngest.Configuration;
using Inngest.Execution;
using Inngest.Registration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inngest.UserContext;

// Both directions of the user context. When a function starts it restores the user from the triggering event into the
// call's accessor. When events are sent it attaches the user: the one the source names when the send is outside a run, or
// the run's own when it is inside one. Like all middleware it is scoped to one call.
internal sealed class UserContextMiddleware(
    UserContextCodec codec,
    UserContextAccessor accessor,
    IOptions<UserContextOptions> options,
    InngestSettings settings,
    IFunctionRegistry registry,
    TimeProvider time,
    ILogger<UserContextMiddleware> logger,
    IUserContextSource? source = null) : InngestMiddleware
{
    private readonly UserContextOptions _options = options.Value;

    // Runs on every call of the run, replays included. The user is judged by when the run was created, so every call
    // gives the same answer and a function that checks the user at its top behaves the same on each replay.
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
        if (!TryFindUser(context, out var user))
        {
            accessor.Set(null);
            return;
        }

        if (!user.Json.TryGetProperty(UserContextCodec.Key, out var envelope))
        {
            accessor.Set(null);
            return;
        }

        // A run's id says when Inngest created it; anything else is judged by the clock of this call.
        var read = codec.Read(envelope, user.EventName, RunIds.StartOf(user.RunId) ?? time.GetUtcNow());
        accessor.Set(read.Snapshot);
        if (read.Snapshot is null)
        {
            Reject(context, user.EventName, read.Problem);
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
        if (registry.TryGet(Compose(context.FunctionId), out var function))
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
        found = new EventUser(user, name, runId);
        return true;
    }

    private bool IsBatched(string functionId) =>
        registry.TryGet(Compose(functionId), out var function) && function.Options.BatchEvents is not null;

    // The registry knows functions by "<app id>-<function id>".
    private string Compose(string functionId) => $"{settings.AppId}-{functionId}";

    private void Reject(FunctionContext context, string eventName, UserReadProblem problem)
    {
        if (_options.OnInvalid == InvalidUserContextAction.FailRun)
        {
            var message = $"The user in event {eventName} cannot be used because {Describe(problem)}.";
            throw problem == UserReadProblem.Unreadable ? new InvalidOperationException(message) : new NonRetriableException(message);
        }

        Log.UserNotUsed(context.Logger, eventName, Describe(problem));
    }

    private static string Describe(UserReadProblem problem) => problem switch
    {
        UserReadProblem.Expired => "it has expired",
        UserReadProblem.WrongEvent => "it was made for another event",
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
            return _options.Propagate ? run.Services.GetService<UserContextAccessor>()?.Snapshot : null;
        }

        return source is null ? null : codec.Capture(await source.GetUserAsync(cancellationToken).ConfigureAwait(false));
    }

    // Each event gets its own protected text, because the text is tied to the event's name; events of one name share it.
    private IReadOnlyList<InngestEvent> Attach(IReadOnlyList<InngestEvent> events, UserSnapshot snapshot)
    {
        InngestEvent[]? attached = null;
        Dictionary<string, string?>? texts = null;
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i] is not { Name: { Length: > 0 } name } @event || _options.EventFilter?.Invoke(@event) == false)
            {
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

    private readonly record struct EventUser(JsonElement Json, string EventName, string RunId);
}
