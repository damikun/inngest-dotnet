// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Inngest.Json;
using Inngest.Signing;

namespace Inngest.Configuration;

internal enum InngestMode
{
    Cloud,
    Dev,
}

// InngestOptions resolved once at startup: environment variables applied, defaults filled in and keys parsed.
// Everything on the request path reads this instead of the mutable options.
internal sealed record InngestSettings
{
    private const string DevVariableName = "INNGEST_DEV";
    private const string EnvironmentVariableName = "INNGEST_ENV";

    private static readonly Uri DefaultApiBaseUrl = new("https://api.inngest.com/");
    private static readonly Uri DefaultEventApiBaseUrl = new("https://inn.gs/");
    private static readonly Uri DefaultDevServerUrl = new("http://localhost:8288/");

    // What hosting platforms expose for the branch, the first one set winning. They only guess the environment, so
    // they come after every way of naming it.
    private static readonly string[] BranchVariableNames =
    [
        "BRANCH_NAME",
        "VERCEL_GIT_COMMIT_REF",
        "BRANCH",
        "CF_PAGES_BRANCH",
        "RENDER_GIT_BRANCH",
        "RAILWAY_GIT_BRANCH",
    ];

    public required string AppId { get; init; }

    public string? AppVersion { get; init; }

    public required InngestMode Mode { get; init; }

    public string? EventKey { get; init; }

    public SigningKey? SigningKey { get; init; }

    public SigningKey? SigningKeyFallback { get; init; }

    public string? Environment { get; init; }

    public required Uri ApiBaseUrl { get; init; }

    public required Uri EventApiBaseUrl { get; init; }

    public Uri? ServeOrigin { get; init; }

    public string? ServePath { get; init; }

    public bool AllowInBandSync { get; init; } = true;

    public bool AllowUnsignedSync { get; init; } = true;

    // Checkpointing for functions whose own options leave it unset; null leaves it off.
    public CheckpointingOptions? Checkpointing { get; init; }

    public required JsonSerializerOptions SerializerOptions { get; init; }

    public bool IsDev => Mode == InngestMode.Dev;

    // Applies the INNGEST_* variables from getVariable (normally IConfiguration or the process environment) to
    // whatever the options leave unset, and then the section in options.Configuration to what is still unset.
    public static InngestSettings Create(InngestOptions options, Func<string, string?> getVariable)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(getVariable);

        // What the configuration section holds, the lowest layer. Read in full, so that a typo fails whatever wins over it.
        var section = options.Configuration is { } configuration ? InngestConfiguration.Read(configuration) : null;
        var appId = string.IsNullOrWhiteSpace(options.AppId) ? section?.AppId : options.AppId;
        if (appId is null)
        {
            throw new InvalidOperationException(
                "InngestOptions.AppId is required. Set it when adding Inngest, for example AddInngest(options => options.AppId = \"my-app\"), or as AppId in the configuration section passed to AddInngest.");
        }

        var variables = new VariableSource(getVariable);
        var (devFlag, devUrlVariable) = ReadDevVariable(variables);
        var isDev = options.IsDev ?? devFlag ?? (devUrlVariable is not null || section?.IsDev == true);

        // The dev server address only applies in dev mode, so IsDev = false keeps an INNGEST_DEV URL from
        // redirecting cloud traffic. The one from code or INNGEST_DEV wins over every address in the section, and the
        // section's own comes after its other addresses, as DevServerUrl does in the options.
        var devUrl = isDev ? RequireHttpUrl(options.DevServerUrl, nameof(InngestOptions.DevServerUrl)) ?? devUrlVariable : null;
        var sectionDevUrl = isDev ? section?.DevServerUrl : null;

        var apiBaseUrl = RequireHttpUrl(options.BaseUrl, nameof(InngestOptions.BaseUrl))
            ?? RequireHttpUrl(options.ApiBaseUrl, nameof(InngestOptions.ApiBaseUrl))
            ?? variables.GetUrl("INNGEST_API_BASE_URL")
            ?? variables.GetUrl("INNGEST_BASE_URL")
            ?? devUrl
            ?? section?.BaseUrl
            ?? section?.ApiBaseUrl
            ?? sectionDevUrl
            ?? (isDev ? DefaultDevServerUrl : DefaultApiBaseUrl);

        var eventApiBaseUrl = RequireHttpUrl(options.BaseUrl, nameof(InngestOptions.BaseUrl))
            ?? RequireHttpUrl(options.EventApiBaseUrl, nameof(InngestOptions.EventApiBaseUrl))
            ?? variables.GetUrl("INNGEST_EVENT_API_BASE_URL")
            ?? variables.GetUrl("INNGEST_BASE_URL")
            ?? devUrl
            ?? section?.BaseUrl
            ?? section?.EventApiBaseUrl
            ?? sectionDevUrl
            ?? (isDev ? DefaultDevServerUrl : DefaultEventApiBaseUrl);

        var serveOrigin = RequireHttpUrl(options.ServeOrigin, nameof(InngestOptions.ServeOrigin))
            ?? variables.GetUrl("INNGEST_SERVE_ORIGIN")
            ?? variables.GetUrl("INNGEST_SERVE_HOST")
            ?? section?.ServeOrigin;

        return new InngestSettings
        {
            AppId = appId,
            AppVersion = NullIfBlank(options.AppVersion) ?? section?.AppVersion,
            Mode = isDev ? InngestMode.Dev : InngestMode.Cloud,
            EventKey = NullIfBlank(options.EventKey) ?? variables.Get("INNGEST_EVENT_KEY") ?? section?.EventKey,
            SigningKey = SigningKey.Create(options.SigningKey)
                ?? SigningKey.Create(variables.Get("INNGEST_SIGNING_KEY"))
                ?? SigningKey.Create(section?.SigningKey),
            SigningKeyFallback = SigningKey.Create(options.SigningKeyFallback)
                ?? SigningKey.Create(variables.Get("INNGEST_SIGNING_KEY_FALLBACK"))
                ?? SigningKey.Create(section?.SigningKeyFallback),

            // A configured name wins over the branch variables of hosting platforms, which only guess.
            Environment = NullIfBlank(options.Environment)
                ?? variables.Get(EnvironmentVariableName)
                ?? section?.Environment
                ?? variables.GetFirst(BranchVariableNames),
            ApiBaseUrl = apiBaseUrl,
            EventApiBaseUrl = eventApiBaseUrl,
            ServeOrigin = serveOrigin,
            ServePath = NormalizePath(NullIfBlank(options.ServePath) ?? variables.Get("INNGEST_SERVE_PATH") ?? section?.ServePath),

            // The option defaults to true, so only false can be told apart from "not set" and win over the variable.
            AllowInBandSync = options.AllowInBandSync
                ?? VariableSource.ParseBoolean(variables.Get("INNGEST_ALLOW_IN_BAND_SYNC"))
                ?? section?.AllowInBandSync
                ?? true,
            AllowUnsignedSync = options.AllowUnsignedSync
                ?? VariableSource.ParseBoolean(variables.Get("INNGEST_ENABLE_UNAUTHED_SYNC"))
                ?? section?.AllowUnsignedSync
                ?? true,
            Checkpointing = RequireValid(options.Checkpointing) ?? section?.Checkpointing,
            SerializerOptions = SerializerOptionsSetup.Prepare(options.SerializerOptions),
        };
    }

    // Printing the settings, for example into a log, must not reveal the event key.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(CultureInfo.InvariantCulture, $"AppId = {AppId}, Mode = {Mode}, ApiBaseUrl = {ApiBaseUrl}, EventApiBaseUrl = {EventApiBaseUrl}, Environment = {Environment}");
        return true;
    }

    // INNGEST_DEV holds a flag or the address of the dev server. A value that is neither is a mistake worth
    // stopping for, because ignoring it would silently run against Inngest Cloud.
    private static (bool? IsDev, Uri? Url) ReadDevVariable(VariableSource variables)
    {
        var value = variables.Get(DevVariableName);
        if (value is null || value.Equals("undefined", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        return VariableSource.ParseBoolean(value) is { } flag ? (flag, null) : (null, VariableSource.ParseUrl(DevVariableName, value));
    }

    private static Uri? RequireHttpUrl(Uri? url, string optionName)
    {
        if (url is null || (url.IsAbsoluteUri && url.Scheme is "http" or "https"))
        {
            return url;
        }

        throw new InvalidOperationException($"InngestOptions.{optionName} must be an absolute http or https URL.");
    }

    private static CheckpointingOptions? RequireValid(CheckpointingOptions? checkpointing) =>
        checkpointing?.FindProblem() is { } problem
            ? throw new InvalidOperationException($"InngestOptions.Checkpointing.{problem}")
            : checkpointing;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizePath(string? path) => path is null || path.StartsWith('/') ? path : "/" + path;
}
