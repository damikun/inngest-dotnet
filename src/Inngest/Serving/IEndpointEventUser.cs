// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;

namespace Inngest.Serving;

// The user that the inngest/http.request event of a durable endpoint request carries, which the run then restores on
// the call made in that request. The user context provides it; without the user context the event has no user.
internal interface IEndpointEventUser
{
    // The "user" of the event that starts a run for the request at timestamp, or null for none.
    ValueTask<JsonElement?> GetAsync(EndpointRequest request, DateTimeOffset timestamp, CancellationToken cancellationToken);
}
