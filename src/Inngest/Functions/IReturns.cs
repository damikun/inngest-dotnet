// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

// What a function class returns, so that a constraint can check it: InngestFunction<TEventData, TResult> implements
// it, and InvokeAsync<TFunction, TResult> requires it.
public interface IReturns<out TResult>;
