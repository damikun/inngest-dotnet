// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest.Functions;

// A registered function: its options plus a way to get an instance for one invocation.
internal sealed class FunctionDefinition(FunctionOptions options, Func<IServiceProvider, InngestFunction> resolve)
{
    public FunctionOptions Options { get; } = options;

    public string Id => Options.Id;

    // Set for the "<id>-failure" companion of a function that handles its own failures. Its trigger depends on the
    // app id, so the registry adds it when the app id is known.
    public string? FailureHandlerFor { get; init; }

    public InngestFunction Resolve(IServiceProvider services) => resolve(services);
}
