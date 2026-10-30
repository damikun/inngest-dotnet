// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

namespace Inngest.UserContext;

// The claims of a user as they were when an event was sent: what travels in the event and what a run restores. Claims
// are grouped by type. The name and role claim types are kept when they are not the .NET defaults, so that a restored
// principal answers Identity.Name and IsInRole as the original did.
internal sealed record UserSnapshot(
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string? NameClaimType,
    string? RoleClaimType,
    IReadOnlyList<KeyValuePair<string, string[]>> Claims);
