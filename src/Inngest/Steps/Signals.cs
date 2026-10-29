// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

// A signal to send to the run that waits for it. Data is serialized with InngestOptions.SerializerOptions using its
// runtime type.
public sealed record InngestSignal(string Name, object? Data = null);

// A signal as the waiting run receives it, with its data deserialized into TData. A signal sent without data has
// JSON null as its data.
public sealed record InngestSignal<TData>(string Name, TData Data);

// Signal is the name the run waits under, such as "approval/order-123". Only one run in an environment can wait for
// a signal at a time; OnConflict decides what happens when another run already does.
public sealed record WaitForSignalOptions(string Signal, TimeSpan Timeout)
{
    public SignalConflict OnConflict { get; init; }
}

public enum SignalConflict
{
    // The run fails when another run already waits for the signal.
    Fail,

    // The run takes the signal over, and the other run's wait stays pending until its timeout.
    Replace,
}
