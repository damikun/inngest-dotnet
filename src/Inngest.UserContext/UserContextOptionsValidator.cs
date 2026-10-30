// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Inngest.UserContext;

// Reports every problem at once, so that the app fails at startup instead of on the first send or call. It also builds the
// protector, which fails when the default one has no Data Protection to use.
internal sealed class UserContextOptionsValidator(IServiceProvider services) : IValidateOptions<UserContextOptions>
{
    // About ten years, which keeps the expiry a date a payload can name.
    private static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(3650);

    public ValidateOptionsResult Validate(string? name, UserContextOptions options)
    {
        var problems = Check(options);
        if (problems.Count > 0)
        {
            return ValidateOptionsResult.Fail(problems);
        }

        _ = services.GetRequiredService<IUserContextProtector>();
        return ValidateOptionsResult.Success;
    }

    public static List<string> Check(UserContextOptions options)
    {
        List<string> problems = [];
        if (options.Claims is not { Count: > 0 })
        {
            problems.Add("UserContextOptions.Claims must list at least one claim type. Without any, no user could be attached.");
        }
        else if (options.Claims.Any(string.IsNullOrWhiteSpace))
        {
            problems.Add("UserContextOptions.Claims must not contain an empty claim type.");
        }

        if (options.Lifetime <= TimeSpan.Zero || options.Lifetime > MaxLifetime)
        {
            problems.Add("UserContextOptions.Lifetime must be more than zero and at most 3650 days.");
        }

        if (options.MaxSize is < 64 or > 1_048_576)
        {
            problems.Add("UserContextOptions.MaxSize must be between 64 bytes and 1 MiB.");
        }

        if (!Enum.IsDefined(options.OnInvalid))
        {
            problems.Add("UserContextOptions.OnInvalid is not a known InvalidUserContextAction.");
        }

        return problems;
    }
}
