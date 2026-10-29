// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text;
using System.Text.Json;
using Inngest.Signing;

namespace Inngest.Internal;

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

    private static readonly Uri DefaultApiBaseUrl = new("https://api.inngest.com/");
    private static readonly Uri DefaultEventApiBaseUrl = new("https://inn.gs/");
    private static readonly Uri DefaultDevServerUrl = new("http://localhost:8288/");

    // The first one set wins, most specific first. The rest are what hosting platforms expose for the branch.
    private static readonly string[] EnvironmentVariableNames =
    [
        "INNGEST_ENV",
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
    // whatever the options leave unset.
    public static InngestSettings Create(InngestOptions options, Func<string, string?> getVariable)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(getVariable);

        if (string.IsNullOrWhiteSpace(options.AppId))
        {
            throw new InvalidOperationException(
                "InngestOptions.AppId is required. Set it when adding Inngest, for example AddInngest(options => options.AppId = \"my-app\").");
        }

        var variables = new VariableSource(getVariable);
        var (devFlag, devUrlVariable) = ReadDevVariable(variables);
        var isDev = options.IsDev ?? devFlag ?? (devUrlVariable is not null);

        // The dev server address only applies in dev mode, so IsDev = false keeps an INNGEST_DEV URL from
        // redirecting cloud traffic.
        var devUrl = isDev ? RequireHttpUrl(options.DevServerUrl, nameof(InngestOptions.DevServerUrl)) ?? devUrlVariable : null;

        var apiBaseUrl = RequireHttpUrl(options.BaseUrl, nameof(InngestOptions.BaseUrl))
            ?? RequireHttpUrl(options.ApiBaseUrl, nameof(InngestOptions.ApiBaseUrl))
            ?? variables.GetUrl("INNGEST_API_BASE_URL")
            ?? variables.GetUrl("INNGEST_BASE_URL")
            ?? devUrl
            ?? (isDev ? DefaultDevServerUrl : DefaultApiBaseUrl);

        var eventApiBaseUrl = RequireHttpUrl(options.BaseUrl, nameof(InngestOptions.BaseUrl))
            ?? RequireHttpUrl(options.EventApiBaseUrl, nameof(InngestOptions.EventApiBaseUrl))
            ?? variables.GetUrl("INNGEST_EVENT_API_BASE_URL")
            ?? variables.GetUrl("INNGEST_BASE_URL")
            ?? devUrl
            ?? (isDev ? DefaultDevServerUrl : DefaultEventApiBaseUrl);

        var serveOrigin = RequireHttpUrl(options.ServeOrigin, nameof(InngestOptions.ServeOrigin))
            ?? variables.GetUrl("INNGEST_SERVE_ORIGIN")
            ?? variables.GetUrl("INNGEST_SERVE_HOST");

        return new InngestSettings
        {
            AppId = options.AppId,
            AppVersion = NullIfBlank(options.AppVersion),
            Mode = isDev ? InngestMode.Dev : InngestMode.Cloud,
            EventKey = NullIfBlank(options.EventKey) ?? variables.Get("INNGEST_EVENT_KEY"),
            SigningKey = SigningKey.Create(options.SigningKey) ?? SigningKey.Create(variables.Get("INNGEST_SIGNING_KEY")),
            SigningKeyFallback = SigningKey.Create(options.SigningKeyFallback)
                ?? SigningKey.Create(variables.Get("INNGEST_SIGNING_KEY_FALLBACK")),
            Environment = NullIfBlank(options.Environment) ?? variables.GetFirst(EnvironmentVariableNames),
            ApiBaseUrl = apiBaseUrl,
            EventApiBaseUrl = eventApiBaseUrl,
            ServeOrigin = serveOrigin,
            ServePath = NormalizePath(NullIfBlank(options.ServePath) ?? variables.Get("INNGEST_SERVE_PATH")),

            // The option defaults to true, so only false can be told apart from "not set" and win over the variable.
            AllowInBandSync = options.AllowInBandSync ?? VariableSource.ParseBoolean(variables.Get("INNGEST_ALLOW_IN_BAND_SYNC")) ?? true,
            AllowUnsignedSync = options.AllowUnsignedSync ?? VariableSource.ParseBoolean(variables.Get("INNGEST_ENABLE_UNAUTHED_SYNC")) ?? true,
            Checkpointing = RequireValid(options.Checkpointing),
            SerializerOptions = SerializerOptionsSetup.Prepare(options.SerializerOptions),
        };
    }

    // Printing the settings, for example into a log, must not reveal the event key.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"AppId = {AppId}, Mode = {Mode}, ApiBaseUrl = {ApiBaseUrl}, EventApiBaseUrl = {EventApiBaseUrl}, Environment = {Environment}");
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
