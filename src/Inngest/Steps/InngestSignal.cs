// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// A signal to send to the run that waits for it. Data is serialized with InngestOptions.SerializerOptions using its
/// runtime type.
/// </summary>
public sealed record InngestSignal(string Name, object? Data = null);

/// <summary>
/// A signal as the waiting run receives it, with its data deserialized into TData. A signal sent without data has
/// JSON null as its data.
/// </summary>
public sealed record InngestSignal<TData>(string Name, TData Data);
