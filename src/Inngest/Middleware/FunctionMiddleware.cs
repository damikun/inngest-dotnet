// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Inngest;

// Middleware for one function, listed in FunctionOptions.Middleware. It runs inside the app's middleware, which wraps
// it, and like app middleware it is resolved from the call's scope, so one instance serves a whole call.
public sealed class FunctionMiddleware
{
    private readonly Action<IServiceCollection>? _register;
    private readonly Func<IServiceProvider, InngestMiddleware> _resolve;

    private FunctionMiddleware(Type? type, Action<IServiceCollection>? register, Func<IServiceProvider, InngestMiddleware> resolve)
    {
        Type = type;
        _register = register;
        _resolve = resolve;
    }

    // Registering the function adds TMiddleware as a scoped service, unless the app registered it already.
    public static FunctionMiddleware Of<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TMiddleware>()
        where TMiddleware : InngestMiddleware =>
        new(
            typeof(TMiddleware),
            static services => services.TryAddScoped<TMiddleware>(),
            static services => services.GetRequiredService<TMiddleware>());

    // Calls the factory with the call's scope on every call.
    public static FunctionMiddleware Of(Func<IServiceProvider, InngestMiddleware> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return new(null, null, factory);
    }

    // Known for Of<TMiddleware>, so that the validator can reject a type listed twice.
    internal Type? Type { get; }

    internal void Register(IServiceCollection services) => _register?.Invoke(services);

    internal InngestMiddleware Resolve(IServiceProvider services) =>
        _resolve(services) ?? throw new InvalidOperationException("A function middleware factory returned null.");
}
