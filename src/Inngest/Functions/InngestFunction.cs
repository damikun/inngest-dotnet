// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Inngest.Functions;

namespace Inngest;

/// <summary>
/// Base class for functions defined as classes. Instances are resolved from a DI scope per invocation, so
/// constructor injection works as usual. Return null when the function has no output.
/// </summary>
public abstract class InngestFunction<TEventData> : InngestFunction<TEventData, object?>;

/// <summary>
/// A function class whose output is a TResult. A function that invokes it by its class receives the result as a
/// TResult, and the compiler checks that both sides agree on the type.
/// </summary>
public abstract class InngestFunction<TEventData, TResult> : InngestFunction, IReturns<TResult>
{
    public abstract Task<TResult> ExecuteAsync(FunctionContext<TEventData> context, CancellationToken cancellationToken);

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

    internal sealed override Task<object?> ExecuteCoreAsync(FunctionContext context, CancellationToken cancellationToken)
    {
        var execution = ExecuteAsync((FunctionContext<TEventData>)context, cancellationToken);

        // A function without a result type of its own already returns a Task<object?>.
        return execution as Task<object?> ?? BoxAsync(execution);
    }

    private static async Task<object?> BoxAsync(Task<TResult> execution) => await execution.ConfigureAwait(false);
}

public abstract class InngestFunction
{
    private protected InngestFunction()
    {
    }

    internal abstract FunctionContext CreateContext(FunctionContextSeed seed);

    internal abstract Task<object?> ExecuteCoreAsync(FunctionContext context, CancellationToken cancellationToken);
}
