// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest.Middleware;

// Middleware that stands in for what lies beyond the SDK, such as the test host answering mocked steps and recording
// sent events instead of sending them. It goes inside a function's own middleware, which then wraps it the way it
// would wrap the real thing.
internal interface IInnermostMiddleware;
