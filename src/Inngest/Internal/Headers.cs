// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest.Internal;

internal static class Headers
{
    public const string Sdk = "X-Inngest-SDK";
    public const string SdkHandled = "X-Inngest-SDK-Handled";
    public const string Signature = "X-Inngest-Signature";
    public const string Environment = "X-Inngest-Env";
    public const string Framework = "X-Inngest-Framework";
    public const string NoRetry = "X-Inngest-No-Retry";
    public const string RequestVersion = "X-Inngest-Req-Version";
    public const string RetryAfter = "Retry-After";
    public const string ServerKind = "X-Inngest-Server-Kind";
    public const string ExpectedServerKind = "X-Inngest-Expected-Server-Kind";
    public const string SyncKind = "X-Inngest-Sync-Kind";
    public const string EventIdSeed = "X-Inngest-Event-Id-Seed";
    public const string StepId = "X-Inngest-Step-ID";
    public const string RequestId = "X-Request-Id";
    public const string JobId = "X-Inngest-Job-Id";
    public const string GenerationId = "X-Inngest-Generation-Id";
    public const string TraceParent = "traceparent";
    public const string TraceState = "tracestate";

    // The execution version this SDK speaks; see docs/design.md.
    public const string ExecutionVersion = "2";
}
