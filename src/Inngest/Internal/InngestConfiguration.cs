// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Inngest.Internal;

// Reads the settings of a configuration section key by key, because binding it would need reflection, which trimmed
// and Native AOT apps cannot rely on. A key that is absent or blank stays null, unknown keys are ignored, and a value
// that cannot be read throws an InvalidOperationException that names the key.
internal static class InngestConfiguration
{
    private const string CheckpointingKey = nameof(InngestOptions.Checkpointing);

    public static InngestOptions Read(IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(section);

        return new InngestOptions
        {
            AppId = ReadText(section, nameof(InngestOptions.AppId)),
            AppVersion = ReadText(section, nameof(InngestOptions.AppVersion)),
            EventKey = ReadText(section, nameof(InngestOptions.EventKey)),
            SigningKey = ReadText(section, nameof(InngestOptions.SigningKey)),
            SigningKeyFallback = ReadText(section, nameof(InngestOptions.SigningKeyFallback)),
            Environment = ReadText(section, nameof(InngestOptions.Environment)),
            IsDev = ReadBoolean(section, nameof(InngestOptions.IsDev)),
            DevServerUrl = ReadUrl(section, nameof(InngestOptions.DevServerUrl)),
            BaseUrl = ReadUrl(section, nameof(InngestOptions.BaseUrl)),
            ApiBaseUrl = ReadUrl(section, nameof(InngestOptions.ApiBaseUrl)),
            EventApiBaseUrl = ReadUrl(section, nameof(InngestOptions.EventApiBaseUrl)),
            ServeOrigin = ReadUrl(section, nameof(InngestOptions.ServeOrigin)),
            ServePath = ReadText(section, nameof(InngestOptions.ServePath)),
            AllowInBandSync = ReadBoolean(section, nameof(InngestOptions.AllowInBandSync)),
            AllowUnsignedSync = ReadBoolean(section, nameof(InngestOptions.AllowUnsignedSync)),
            Checkpointing = ReadCheckpointing(section),
        };
    }

    // The startup check only needs to know whether the section names the app, whatever else it holds.
    public static string? ReadAppId(IConfiguration section) => ReadText(section, nameof(InngestOptions.AppId));

    private static CheckpointingOptions? ReadCheckpointing(IConfiguration section)
    {
        // A plain value such as "Checkpointing": true would otherwise be ignored without a word.
        if (ReadText(section, CheckpointingKey) is { } scalar)
        {
            throw Invalid(section, CheckpointingKey, "a section with Enabled, MaxRuntime, BufferedSteps or MaxInterval", scalar);
        }

        var checkpointing = section.GetSection(CheckpointingKey);
        var enabled = ReadBoolean(checkpointing, nameof(CheckpointingOptions.Enabled));
        var maxRuntime = ReadDuration(checkpointing, nameof(CheckpointingOptions.MaxRuntime));
        var bufferedSteps = ReadInteger(checkpointing, nameof(CheckpointingOptions.BufferedSteps));
        var maxInterval = ReadDuration(checkpointing, nameof(CheckpointingOptions.MaxInterval));
        if (enabled is null && maxRuntime is null && bufferedSteps is null && maxInterval is null)
        {
            return null;
        }

        var defaults = new CheckpointingOptions();
        var options = new CheckpointingOptions
        {
            Enabled = enabled ?? defaults.Enabled,
            MaxRuntime = maxRuntime ?? defaults.MaxRuntime,
            BufferedSteps = bufferedSteps ?? defaults.BufferedSteps,
            MaxInterval = maxInterval ?? defaults.MaxInterval,
        };

        return options.FindProblem() is { } problem
            ? throw new InvalidOperationException($"{KeyPath(section, CheckpointingKey)}:{problem}")
            : options;
    }

    private static string? ReadText(IConfiguration section, string key)
    {
        var value = section[key]?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static bool? ReadBoolean(IConfiguration section, string key) =>
        ReadText(section, key) is { } value
            ? VariableSource.ParseBoolean(value) ?? throw Invalid(section, key, "true or false", value)
            : null;

    private static Uri? ReadUrl(IConfiguration section, string key)
    {
        if (ReadText(section, key) is not { } value)
        {
            return null;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme is "http" or "https"
            ? url
            : throw Invalid(section, key, "an absolute http or https URL", value);
    }

    private static int? ReadInteger(IConfiguration section, string key)
    {
        if (ReadText(section, key) is not { } value)
        {
            return null;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw Invalid(section, key, "a whole number", value);
    }

    // Inngest time strings such as "30s" or "1h30m", or the form TimeSpan writes, such as "00:00:30". The second needs
    // a colon, because TimeSpan reads a bare "30" as 30 days.
    private static TimeSpan? ReadDuration(IConfiguration section, string key)
    {
        if (ReadText(section, key) is not { } value)
        {
            return null;
        }

        if (TimeString.TryParse(value, out var duration) ||
            (value.Contains(':') && TimeSpan.TryParseExact(value, "c", CultureInfo.InvariantCulture, out duration)))
        {
            return duration;
        }

        throw Invalid(section, key, "a duration such as 30s, 1h30m or 00:00:30", value);
    }

    private static InvalidOperationException Invalid(IConfiguration section, string key, string expected, string value) =>
        new($"{KeyPath(section, key)} must be {expected}, but it is '{value}'.");

    // The key as the configuration knows it, such as "Inngest:IsDev"; the root has no path to put in front.
    private static string KeyPath(IConfiguration section, string key) =>
        section is IConfigurationSection { Path: { Length: > 0 } path } ? ConfigurationPath.Combine(path, key) : key;
}
