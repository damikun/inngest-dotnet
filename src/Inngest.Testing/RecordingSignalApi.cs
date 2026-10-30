// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using Inngest.Client;

namespace Inngest.Testing;

// Takes the place of POST /v1/signals. The host runs one function at a time, so no other run can be waiting for the
// signal: it is recorded, and the client hears what Inngest answers when no run waits.
internal sealed class RecordingSignalApi(InngestTestHost host) : ISignalApi
{
    public Task<string?> SendAsync(string signal, ReadOnlyMemory<byte> body, string? environment, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(body);
        var data = document.RootElement.TryGetProperty("data", out var value) ? value.Clone() : JsonDocument.Parse("null").RootElement;
        host.RecordSignal(new InngestSignal<JsonElement>(signal, data), RunDriver.Current);
        return Task.FromResult<string?>(null);
    }
}
