// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Inngest.Functions;

namespace Inngest;

// Base class for functions defined as classes. Instances are resolved from a DI scope per invocation, so
// constructor injection works as usual. Return null when the function has no output.
public abstract class InngestFunction<TEventData> : InngestFunction
{
    public abstract Task<object?> ExecuteAsync(FunctionContext<TEventData> context, CancellationToken cancellationToken);

    internal sealed override FunctionContext CreateContext(FunctionContextSeed seed)
    {
        // Each event is read once, so that payload transforms see it once; without a batch, Event is the only one.
        InngestEvent<TEventData>[] events;
        if (seed.Events.Count == 0)
        {
            events = [EventReader.Read<TEventData>(seed.Event, seed.SerializerOptions, seed.Transforms)];
        }
        else
        {
            events = new InngestEvent<TEventData>[seed.Events.Count];
            for (var i = 0; i < events.Length; i++)
            {
                events[i] = EventReader.Read<TEventData>(seed.Events[i], seed.SerializerOptions, seed.Transforms);
            }
        }

        return new FunctionContext<TEventData>
        {
            FunctionId = seed.FunctionId,
            RunId = seed.RunId,
            Attempt = seed.Attempt,
            MaxAttempts = seed.MaxAttempts,
            Step = seed.Step,
            Logger = seed.Logger,
            Services = seed.Services,
            CancellationToken = seed.CancellationToken,
            RequestId = seed.RequestId,
            JobId = seed.JobId,
            Middleware = seed.Middleware,
            EventSource = seed.Events.Count > 0 ? seed.Events[0] : seed.Event,
            Event = events[0],
            Events = events,
        };
    }

    internal sealed override Task<object?> ExecuteCoreAsync(FunctionContext context, CancellationToken cancellationToken) =>
        ExecuteAsync((FunctionContext<TEventData>)context, cancellationToken);
}

public abstract class InngestFunction
{
    private protected InngestFunction()
    {
    }

    internal abstract FunctionContext CreateContext(FunctionContextSeed seed);

    internal abstract Task<object?> ExecuteCoreAsync(FunctionContext context, CancellationToken cancellationToken);
}
