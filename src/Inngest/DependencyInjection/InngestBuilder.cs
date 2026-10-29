// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Microsoft.Extensions.DependencyInjection;

namespace Inngest.DependencyInjection;

internal sealed class InngestBuilder(IServiceCollection services) : IInngestBuilder
{
    public IServiceCollection Services { get; } = services;
}
