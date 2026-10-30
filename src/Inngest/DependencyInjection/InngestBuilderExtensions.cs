// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Inngest.Functions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Inngest;

public static class InngestBuilderExtensions
{
    /// <summary>
    /// Registers a function class configured by its [InngestFunction], trigger and flow control attributes, which are
    /// read once, here.
    /// </summary>
    public static IInngestBuilder AddFunction<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TFunction>(
        this IInngestBuilder builder)
        where TFunction : InngestFunction
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddFunction<TFunction>(FunctionAttributes.Read(typeof(TFunction)));
    }

    /// <summary>
    /// Registers a function class with the options its attributes give, as changed by configure, for example to apply
    /// a limit read from configuration: options =&gt; options with { Concurrency = [new ConcurrencyLimit(limit)] }.
    /// </summary>
    public static IInngestBuilder AddFunction<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TFunction>(
        this IInngestBuilder builder,
        Func<FunctionOptions, FunctionOptions> configure)
        where TFunction : InngestFunction
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var options = configure(FunctionAttributes.Read(typeof(TFunction)))
            ?? throw new InvalidOperationException($"The configure callback for {typeof(TFunction).Name} returned null instead of the function's options.");

        return builder.AddFunction<TFunction>(options);
    }

    /// <summary>
    /// Registers a class-based function with these options; attributes on the class are ignored. A new instance is
    /// resolved from the invocation's scope every time.
    /// </summary>
    public static IInngestBuilder AddFunction<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TFunction>(
        this IInngestBuilder builder,
        FunctionOptions options)
        where TFunction : InngestFunction
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        builder.Services.TryAddTransient<TFunction>();
        AddFunctionMiddleware(builder.Services, options);
        builder.Services.AddSingleton(new FunctionDefinition(options, static services => services.GetRequiredService<TFunction>())
        {
            FunctionType = typeof(TFunction),
        });
        if (typeof(IFailureHandler).IsAssignableFrom(typeof(TFunction)))
        {
            builder.Services.AddSingleton(new FunctionDefinition(
                FailureFunction.CreateOptions(options),
                static services => new FailureFunction(((IFailureHandler)services.GetRequiredService<TFunction>()).OnFailureAsync))
            {
                FailureHandlerFor = options.Id,
            });
        }

        return builder;
    }

    public static IInngestBuilder AddFunction<TEventData>(
        this IInngestBuilder builder,
        FunctionOptions options,
        Func<FunctionContext<TEventData>, CancellationToken, Task<object?>> handler,
        Func<FunctionContext<FunctionFailure>, CancellationToken, Task>? onFailure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(handler);
        var function = new DelegateFunction<TEventData>(handler);
        AddFunctionMiddleware(builder.Services, options);
        builder.Services.AddSingleton(new FunctionDefinition(options, _ => function));
        if (onFailure is not null)
        {
            var failure = new FailureFunction(onFailure);
            builder.Services.AddSingleton(new FunctionDefinition(FailureFunction.CreateOptions(options), _ => failure)
            {
                FailureHandlerFor = options.Id,
            });
        }

        return builder;
    }

    public static IInngestBuilder AddFunction(
        this IInngestBuilder builder,
        FunctionOptions options,
        Func<FunctionContext<JsonElement>, CancellationToken, Task<object?>> handler,
        Func<FunctionContext<FunctionFailure>, CancellationToken, Task>? onFailure = null) =>
        builder.AddFunction<JsonElement>(options, handler, onFailure);

    /// <summary>
    /// Middleware runs in registration order. It is scoped: one instance serves one call from Inngest, including the
    /// events its steps send, so it can keep state for that call. The calls of a run can reach any instance of the app,
    /// so nothing kept in fields lasts the run.
    /// </summary>
    public static IInngestBuilder AddMiddleware<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TMiddleware>(
        this IInngestBuilder builder)
        where TMiddleware : InngestMiddleware
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<InngestMiddleware, TMiddleware>());
        return builder;
    }

    /// <summary>
    /// Registers one middleware instance for every call, including calls that run at the same time, so it must be
    /// thread-safe and keep no state for a call in fields.
    /// </summary>
    public static IInngestBuilder AddMiddleware(this IInngestBuilder builder, InngestMiddleware middleware)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(middleware);
        builder.Services.AddSingleton(middleware);
        return builder;
    }

    // Null entries are left to the validator, which names the function when the app starts.
    private static void AddFunctionMiddleware(IServiceCollection services, FunctionOptions options)
    {
        foreach (var middleware in options.Middleware ?? [])
        {
            middleware?.Register(services);
        }
    }
}
