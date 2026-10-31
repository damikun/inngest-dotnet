// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Security.Claims;

namespace Inngest;

/// <summary>
/// Settings of the user context. The apps that send users and the apps that restore them need the same protector, which
/// by default means the same Data Protection key ring and application name.
/// </summary>
public sealed class UserContextOptions
{
    /// <summary>
    /// The claim types that are attached, matched exactly as they appear in the principal. Every other claim is left out,
    /// and so is the rest of the principal. Claims travel in every event and anyone who can see the event sees where they
    /// are, so list only what the runs need. The default is "sub" and ClaimTypes.NameIdentifier. A claim type that the
    /// legacy JWT handler maps to a long URI is listed in the form the principal has. Only the sending side reads this:
    /// a run restores whatever a valid payload holds.
    /// </summary>
    public IReadOnlyList<string> Claims { get; set; } = ["sub", ClaimTypes.NameIdentifier];

    /// <summary>
    /// How long after the claims were taken a run can still start with them: seven days by default. A run is judged once,
    /// by the later of its creation, which its id says, and the timestamp of its event, not at each call. So a run that
    /// started in time keeps its user while it sleeps, waits and retries, and the user cannot change under it halfway,
    /// while an event scheduled further ahead than the lifetime carries no user. Events and invocations a run sends carry
    /// the same expiry, so a chain of runs cannot extend it. A longer lifetime lets a payload that leaked from an event
    /// start runs for longer, a shorter one cuts off what is delayed: debounced and scheduled runs.
    /// </summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// The largest size, in UTF-8 bytes, of the JSON that holds the claims before it is protected; 4096 by default.
    /// Claims over it are not attached: the event is sent without a user and a warning is logged. The protected text
    /// is about a third larger, and an event can be 256 KiB on Inngest's Free plan.
    /// </summary>
    public int MaxSize { get; set; } = 4096;

    /// <summary>
    /// Whether the events that a run sends carry the run's user, as they carry its sessions; true by default. The run's
    /// user keeps its original expiry. An invocation carries it only when the code asks with
    /// <see cref="UserContextInvokeExtensions.WithUser"/>.
    /// </summary>
    public bool Propagate { get; set; } = true;

    /// <summary>
    /// Decides for each event that is sent whether it carries the user; null, the default, attaches it to every event.
    /// Return false for events that do not need to know who caused them.
    /// </summary>
    public Func<InngestEvent, bool>? EventFilter { get; set; }

    /// <summary>
    /// What a run does with a user it cannot use: it has expired, was changed, was made with a key this app lacks or
    /// for another event. The default runs without a user and writes a warning.
    /// </summary>
    public InvalidUserContextAction OnInvalid { get; set; } = InvalidUserContextAction.Anonymous;
}
