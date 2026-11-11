// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest.Middleware;

// Middleware that keeps payloads out of Inngest's reach, such as the encryption middleware. What it cannot cover is
// then left out of what the SDK sends, such as the input the steps of Inngest.Extensions.AI record.
internal interface IEncryptingMiddleware;
