// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Inngest.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Inngest.DependencyInjection;

// Builds the settings from the options the way the app will, so that a value the SDK cannot read, such as an
// INNGEST_EVENT_API_BASE_URL that is not an http URL, fails the host at startup. An app that only sends events would
// otherwise start, pass its health checks and fail every send.
internal sealed class InngestOptionsValidator(IServiceProvider services) : IValidateOptions<InngestOptions>
{
    public ValidateOptionsResult Validate(string? name, InngestOptions options)
    {
        // A missing app id has a check and a message of its own.
        if (name != Options.DefaultName || !HasAppId(options))
        {
            return ValidateOptionsResult.Skip;
        }

        try
        {
            InngestSettings.Create(options, VariablesOf(services));
            return ValidateOptionsResult.Success;
        }
        catch (InvalidOperationException exception)
        {
            return ValidateOptionsResult.Fail(exception.Message);
        }
    }

    public static bool HasAppId(InngestOptions options) =>
        !string.IsNullOrWhiteSpace(options.AppId) || (options.Configuration is { } section && InngestConfiguration.ReadAppId(section) is not null);

    // The INNGEST_* variables come from IConfiguration when the host has one, else from the process environment.
    public static Func<string, string?> VariablesOf(IServiceProvider services) =>
        services.GetService<IConfiguration>() is { } configuration ? name => configuration[name] : Environment.GetEnvironmentVariable;
}
