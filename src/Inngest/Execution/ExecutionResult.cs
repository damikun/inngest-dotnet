// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Inngest.Internal;

namespace Inngest.Execution;

// What the engine decided, already shaped for the wire: 200 with the function output, 206 with step operations,
// or 400/500 with a serialized error. Dispose after the body has been written.
internal sealed class ExecutionResult(int statusCode, PooledBufferWriter body) : IDisposable
{
    public int StatusCode { get; } = statusCode;

    public PooledBufferWriter Body { get; } = body;

    // Sent as X-Inngest-No-Retry on errors; null on success.
    public bool? NoRetry { get; init; }

    // Sent as Retry-After when the error asked for a specific retry time.
    public string? RetryAfter { get; init; }

    // What became of a durable endpoint request that started a run; null for every call from Inngest. Its status and
    // body are then empty, since the request is answered by the endpoint's host.
    public EndpointOutcome? Endpoint { get; init; }

    public void Dispose() => Body.Dispose();

    public static ExecutionResult ForEndpoint(EndpointOutcome outcome) => new(200, new PooledBufferWriter(0)) { Endpoint = outcome };
}
