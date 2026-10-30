// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Text.Json;

namespace Inngest.AspNetCore;

internal enum SyncVerdict
{
    Synced,
    NotRegistered,
    Archived,
    Failed,
    VersionMismatch,
    FunctionCountMismatch,
    Unreadable,
}

// What Inngest says about the app and whether it matches this instance. The texts are Inngest's own, cut short; none
// of them comes from the request that was sent.
internal sealed record SyncResult(SyncVerdict Verdict, int? FunctionCount = null, string? Status = null, string? SyncedAt = null, string? SyncedVersion = null)
{
    private const int MaxTextLength = 64;

    public bool IsSynced => Verdict == SyncVerdict.Synced;

    // Judges the body of Inngest's answer to GET /v2/apps/{app id}, a Beta endpoint, against the functions and the
    // version this instance serves. The dev server leaves out what is false, zero or empty, and does not report a
    // sync's status or time; Inngest Cloud reports both.
    public static SyncResult Read(JsonElement body, int servedFunctions, string? servedVersion)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("data", out var app) || app.ValueKind != JsonValueKind.Object)
        {
            return new SyncResult(SyncVerdict.Unreadable);
        }

        var functionCount = app.TryGetProperty("functionCount", out var count) && count.TryGetInt32(out var number) ? number : 0;
        var archived = app.TryGetProperty("isArchived", out var flag) && flag.ValueKind == JsonValueKind.True;

        string? status = null, syncedAt = null, error = null, syncedVersion = null;
        if (app.TryGetProperty("latestSync", out var latest) && latest.ValueKind == JsonValueKind.Object)
        {
            status = Text(latest, "status");
            syncedAt = Text(latest, "syncedAt");
            error = Text(latest, "error");
            syncedVersion = Text(latest, "appVersion");
        }

        syncedVersion ??= Text(app, "appVersion");

        // A status of "error" says the latest sync failed. Without a status, which is how the dev server answers, an
        // error is all there is to go on.
        var verdict = archived ? SyncVerdict.Archived
            : status == "error" || (status is null && error is not null) ? SyncVerdict.Failed
            : servedVersion is not null && syncedVersion is not null && !string.Equals(servedVersion, syncedVersion, StringComparison.Ordinal) ? SyncVerdict.VersionMismatch
            : functionCount != servedFunctions ? SyncVerdict.FunctionCountMismatch
            : SyncVerdict.Synced;

        return new SyncResult(verdict, functionCount, status, syncedAt, syncedVersion);
    }

    private static string? Text(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } text)
        {
            return null;
        }

        return text.Length <= MaxTextLength ? text : text[..MaxTextLength] + "...";
    }
}
