// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Globalization;
using Inngest.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Inngest.AspNetCore;

// Whether this app can work with Inngest: its keys are configured, and Inngest answers and takes the signing key. The
// result says what was found in its data and names the first problem in its description, with the registration's
// failure status. Nothing in either holds a key, or anything derived from one.
internal sealed class InngestHealthCheck(InngestSettings settings, InngestHealthPolicy policy, InngestHealthProbe probe) : IHealthCheck
{
    // The share of the registration's timeout that the request to Inngest may use. The health check service cancels
    // the check at the timeout and then reports "unhealthy" in its own words; the rest lets the check say that Inngest
    // was too slow, with the failure status the registration asks for.
    private const double TimeoutShare = 0.9;

    // A registration has no timeout unless it is given one, and a request to a service that never answers would then
    // hold the probes for as long as HttpClient waits, which is 100 seconds.
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var failureStatus = context.Registration.FailureStatus;
        var data = new Dictionary<string, object>
        {
            ["mode"] = settings.IsDev ? "dev" : "cloud",
            ["apiBaseUrl"] = Origin(settings.ApiBaseUrl),
        };

        // Without a key the request cannot succeed, so it is not sent.
        if (CheckConfiguration(data) is { } problem)
        {
            data["api"] = "not checked";
            return new HealthCheckResult(failureStatus, problem, data: data);
        }

        var result = await probe.GetAsync(RequestTimeout(context.Registration.Timeout), cancellationToken).ConfigureAwait(false);
        data["api"] = Name(result.Api);
        if (result.StatusCode != 0)
        {
            data["apiStatusCode"] = result.StatusCode;
        }

        if (result.Api == ApiStatus.Ok && !settings.IsDev)
        {
            data["acceptedKey"] = result.UsedFallbackKey ? "signingKeyFallback" : "signingKey";
        }

        data["checkedAt"] = result.CheckedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        // The sync is only known when the API answered, and its problems are those of the app, not of the API.
        if (result.Sync is { } sync)
        {
            AddSync(data, sync);
            return sync.IsSynced
                ? HealthCheckResult.Healthy($"{Describe(result)} {Describe(sync)}", data)
                : new HealthCheckResult(failureStatus, Describe(sync), data: data);
        }

        return new HealthCheckResult(StatusOf(result, failureStatus), Describe(result), data: data);
    }

    // Records what the configuration holds, as "set", "not set" or "missing" and never as a value, and returns the
    // problems that make the check fail, or null.
    private string? CheckConfiguration(Dictionary<string, object> data)
    {
        List<string>? problems = null;

        if (settings.SigningKey is not null)
        {
            data["signingKey"] = "set";
        }
        else if (settings.IsDev)
        {
            data["signingKey"] = "not set";
        }
        else
        {
            data["signingKey"] = "missing";
            (problems ??= []).Add("No signing key is configured, and Inngest refuses requests without one outside dev mode. Set INNGEST_SIGNING_KEY or InngestOptions.SigningKey.");
        }

        data["signingKeyFallback"] = settings.SigningKeyFallback is null ? "not set" : "set";

        if (settings.EventKey is not null)
        {
            data["eventKey"] = "set";
        }
        else if (policy.RequireEventKey && !settings.IsDev)
        {
            data["eventKey"] = "missing";
            (problems ??= []).Add("No event key is configured, so the app cannot send events. Set INNGEST_EVENT_KEY or InngestOptions.EventKey.");
        }
        else
        {
            data["eventKey"] = "not set";
        }

        return problems is null ? null : string.Join(' ', problems);
    }

    private void AddSync(Dictionary<string, object> data, SyncResult sync)
    {
        data["sync"] = sync.Verdict switch
        {
            SyncVerdict.Synced => "synced",
            SyncVerdict.NotRegistered => "not registered",
            SyncVerdict.Archived => "archived",
            SyncVerdict.Failed => "failed",
            SyncVerdict.VersionMismatch => "version mismatch",
            SyncVerdict.FunctionCountMismatch => "function count mismatch",
            _ => "unreadable",
        };

        data["functionsServed"] = probe.ServedFunctions;
        AddIfKnown(data, "functionsInInngest", sync.FunctionCount);
        AddIfKnown(data, "syncStatus", sync.Status);
        AddIfKnown(data, "syncedAt", sync.SyncedAt);
        AddIfKnown(data, "appVersion", settings.AppVersion);
        AddIfKnown(data, "syncedAppVersion", sync.SyncedVersion);
    }

    private static string Functions(int? count) => count == 1 ? "1 function" : $"{count ?? 0} functions";

    private static void AddIfKnown(Dictionary<string, object> data, string name, object? value)
    {
        if (value is not null)
        {
            data[name] = value;
        }
    }

    private string Describe(SyncResult sync) => sync.Verdict switch
    {
        SyncVerdict.Synced => $"The app \"{settings.AppId}\" is synced with {Functions(sync.FunctionCount)}.",
        SyncVerdict.NotRegistered => $"Inngest has no app \"{settings.AppId}\". Sync the app so that Inngest knows its functions.",
        SyncVerdict.Archived => $"The app \"{settings.AppId}\" is archived in Inngest.",
        SyncVerdict.Failed => $"Inngest reports that the latest sync of the app \"{settings.AppId}\" failed.",
        SyncVerdict.VersionMismatch =>
            $"Inngest last synced version \"{sync.SyncedVersion}\" of the app \"{settings.AppId}\", and this instance is version \"{settings.AppVersion}\". Sync the app.",
        SyncVerdict.FunctionCountMismatch =>
            $"Inngest has {Functions(sync.FunctionCount)} for the app \"{settings.AppId}\", and this instance serves {probe.ServedFunctions}. Sync the app.",
        _ => $"Inngest's answer about the app \"{settings.AppId}\" could not be read.",
    };

    private static HealthStatus StatusOf(ProbeResult result, HealthStatus failureStatus) => result.Api switch
    {
        ApiStatus.Ok => HealthStatus.Healthy,

        // Inngest answers, so it is there, and a rate limit says nothing about the key. Taking every replica out of
        // rotation because the probes themselves ask too often would turn a warning into an outage. The failure
        // status wins when it is milder than Degraded.
        ApiStatus.RateLimited => (HealthStatus)Math.Max((int)failureStatus, (int)HealthStatus.Degraded),
        _ => failureStatus,
    };

    private string Describe(ProbeResult result) => result.Api switch
    {
        ApiStatus.Ok when settings.IsDev => "Reached the Inngest dev server.",
        ApiStatus.Ok when result.UsedFallbackKey => "Inngest refused the signing key and accepted the fallback signing key.",
        ApiStatus.Ok => "Inngest accepted the signing key.",
        ApiStatus.Refused when settings.SigningKey is null =>
            $"Inngest requires a signing key (HTTP {result.StatusCode}) and none is configured. Set INNGEST_SIGNING_KEY or InngestOptions.SigningKey.",
        ApiStatus.Refused when result.UsedFallbackKey => $"Inngest refused the signing key and the fallback signing key (HTTP {result.StatusCode}).",
        ApiStatus.Refused => $"Inngest refused the signing key (HTTP {result.StatusCode}).",
        ApiStatus.Unreachable => $"Could not reach Inngest at {Origin(settings.ApiBaseUrl)}: {EndWithPeriod(result.Detail)}",
        ApiStatus.TimedOut => $"Inngest at {Origin(settings.ApiBaseUrl)} did not answer within the timeout of the check.",
        ApiStatus.RateLimited => "Inngest rate limited the request (HTTP 429), so the signing key was not checked this time.",
        _ => $"Inngest at {Origin(settings.ApiBaseUrl)} answered with HTTP {result.StatusCode}.",
    };

    private static string Name(ApiStatus status) => status switch
    {
        ApiStatus.Ok => "ok",
        ApiStatus.Refused => "refused",
        ApiStatus.Unreachable => "unreachable",
        ApiStatus.TimedOut => "timed out",
        ApiStatus.RateLimited => "rate limited",
        _ => "unexpected response",
    };

    // Scheme, host and port only. A base URL may carry a user name, a password or a query, and none of it belongs in
    // a health report.
    private static string Origin(Uri url) => $"{url.Scheme}://{url.Authority}";

    private static string EndWithPeriod(string? text)
    {
        var trimmed = text?.TrimEnd();
        return string.IsNullOrEmpty(trimmed) ? "no reason given." : trimmed.EndsWith('.') ? trimmed : trimmed + ".";
    }

    private static TimeSpan RequestTimeout(TimeSpan registrationTimeout) =>
        registrationTimeout <= TimeSpan.Zero
            ? DefaultTimeout
            : TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerMillisecond, (long)(registrationTimeout.Ticks * TimeoutShare)));
}
