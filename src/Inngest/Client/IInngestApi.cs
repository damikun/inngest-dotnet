// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;

namespace Inngest.Client;

// The parts of the Inngest REST API the SDK calls. Requests carry the hashed signing key as a bearer token and
// are retried once with the fallback key on 401 or 403.
internal interface IInngestApi
{
    // POST {api}/fn/register for an out-of-band sync. serverKind is copied into X-Inngest-Expected-Server-Kind.
    // A rejected sync or an unreachable Inngest comes back as a failed result, never as an exception.
    Task<RegisterResult> RegisterAsync(ReadOnlyMemory<byte> payload, string? deployId, string? serverKind, CancellationToken cancellationToken);

    // GET {api}/v0/runs/{runId}/actions: the memoized steps of a run, for requests with use_api set. Throws an
    // InngestException with Inngest's message unless Inngest answers 200. The caller disposes the document.
    Task<JsonDocument> GetRunStepsAsync(string runId, CancellationToken cancellationToken);

    // GET {api}/v0/runs/{runId}/batch: the events of a run, for requests with use_api set. Fails like
    // GetRunStepsAsync.
    Task<JsonDocument> GetRunEventsAsync(string runId, CancellationToken cancellationToken);
}

// StatusCode is the status Inngest answered with, or 500 when there was no answer.
internal sealed record RegisterResult(bool Success, int StatusCode, bool Modified, string? Error);
