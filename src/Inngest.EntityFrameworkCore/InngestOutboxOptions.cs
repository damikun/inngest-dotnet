// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

namespace Inngest;

/// <summary>
/// Settings of the outbox of one DbContext, set with AddEntityFrameworkOutbox. The defaults suit most apps. Every
/// instance of the app that registers the outbox runs a dispatcher, so these settings should be the same everywhere.
/// </summary>
public sealed class InngestOutboxOptions
{
    /// <summary>
    /// Runs the dispatcher in this process. Turn it off in a process that only saves events, for example one that
    /// cannot stay alive to send them, and keep it on in at least one other. Defaults to true.
    /// </summary>
    public bool DispatcherEnabled { get; set; } = true;

    /// <summary>
    /// How long the dispatcher waits for new events before it looks again. A save through the same app wakes it at
    /// once, so this mostly bounds how late it finds events that other processes saved and retries that fell due.
    /// Defaults to 5 seconds.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The most events one dispatcher claims, and sends in one request, at a time. Defaults to 100.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// How long a dispatcher owns the events it claimed. When it dies before it marked them, they are sent again by
    /// another dispatcher after this time, under the same ids, which Inngest is documented to drop. At least twice
    /// <see cref="SendTimeout"/>. Instances compare their clocks with the expiry another one wrote, so clocks that differ
    /// by more than this minus <see cref="SendTimeout"/> let one take events that another is still sending. Defaults to
    /// 1 minute.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long one request to Inngest may take, retries of the SDK's client included, before it counts as failed.
    /// Defaults to 20 seconds.
    /// </summary>
    public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long an event waits after its first failed attempt. Every further failure doubles it, up to
    /// <see cref="MaxRetryDelay"/>. Defaults to 5 seconds.
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The longest an event waits between attempts. An event is never given up: it stays in the table and is tried
    /// again at this interval for as long as it fails. Defaults to 5 minutes.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long events that Inngest accepted stay in the table before they are deleted. Keep it at least as long as
    /// Inngest remembers event ids (24 hours), so that an id given by the code cannot be added again while Inngest
    /// would still drop it. Zero deletes them on the next cleanup. Defaults to 1 day.
    /// </summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(1);

    /// <summary>How often the dispatcher deletes events that are older than <see cref="Retention"/>. Defaults to 5 minutes.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How many events one DELETE removes, so that a large backlog never holds locks for long. Defaults to 1000.</summary>
    public int CleanupBatchSize { get; set; } = 1000;
}
