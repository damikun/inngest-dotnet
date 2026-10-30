// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// A realtime topic whose messages carry a T, so that the compiler checks what is published to it and subscribers
/// read it as a T. It is a topic of its own type rather than a RealtimeTopic, so data of another type does not compile.
/// </summary>
public sealed record RealtimeTopic<T>(string Channel, string Name)
{
    /// <summary>The same topic without its data type, for the members that take any topic.</summary>
    public RealtimeTopic Untyped => new(Channel, Name);
}
