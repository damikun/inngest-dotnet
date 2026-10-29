// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Microsoft.Extensions.Time.Testing;

namespace Inngest.Testing;

// The time of a run as the host simulates it: a sleep or a timeout completes at the time it would complete at, in
// that order, without waiting for it. It starts at the app's time and moves only when something completes, so
// the order does not depend on how fast the test runs. An app on a FakeTimeProvider sees its clock move along.
internal sealed class RunClock(TimeProvider time)
{
    private DateTimeOffset _simulated = time.GetUtcNow();

    // A FakeTimeProvider can also be moved by the test's own code.
    public DateTimeOffset Now =>
        time is FakeTimeProvider fake && fake.GetUtcNow() > _simulated ? fake.GetUtcNow() : _simulated;

    public void AdvanceTo(DateTimeOffset instant)
    {
        if (instant <= Now)
        {
            return;
        }

        _simulated = instant;
        if (time is FakeTimeProvider fake)
        {
            fake.SetUtcNow(instant);
        }
    }
}
