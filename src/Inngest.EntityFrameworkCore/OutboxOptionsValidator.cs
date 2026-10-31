// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Microsoft.Extensions.Options;

namespace Inngest.EntityFrameworkCore;

// Reports every problem at once, so that the app fails at startup and not in the middle of the first pass. The name of
// the options is the type of the DbContext they belong to.
internal sealed class OutboxOptionsValidator : IValidateOptions<InngestOutboxOptions>
{
    public ValidateOptionsResult Validate(string? name, InngestOutboxOptions options)
    {
        List<string> failures = [];
        var owner = $"InngestOutboxOptions of {name}";

        if (options.PollInterval <= TimeSpan.Zero)
        {
            failures.Add($"{owner}: PollInterval must be longer than zero.");
        }

        if (options.BatchSize < 1)
        {
            failures.Add($"{owner}: BatchSize must be at least 1.");
        }

        if (options.SendTimeout <= TimeSpan.Zero)
        {
            failures.Add($"{owner}: SendTimeout must be longer than zero.");
        }

        // One request has to fit in the lease, and so does a second: when Inngest refuses a request after taking its time, the
        // events are sent one at a time, and with less than this the lease is gone before the first of those requests.
        if (options.LeaseDuration < options.SendTimeout * 2)
        {
            failures.Add($"{owner}: LeaseDuration must be at least twice SendTimeout, so that a request Inngest refuses can be followed by requests for single events before the lease ends.");
        }

        if (options.RetryDelay <= TimeSpan.Zero)
        {
            failures.Add($"{owner}: RetryDelay must be longer than zero.");
        }

        if (options.MaxRetryDelay < options.RetryDelay)
        {
            failures.Add($"{owner}: MaxRetryDelay must not be shorter than RetryDelay.");
        }

        if (options.Retention < TimeSpan.Zero)
        {
            failures.Add($"{owner}: Retention must not be negative.");
        }

        if (options.CleanupInterval <= TimeSpan.Zero)
        {
            failures.Add($"{owner}: CleanupInterval must be longer than zero.");
        }

        if (options.CleanupBatchSize < 1)
        {
            failures.Add($"{owner}: CleanupBatchSize must be at least 1.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
