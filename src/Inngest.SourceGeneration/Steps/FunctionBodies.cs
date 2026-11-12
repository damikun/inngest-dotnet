// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Inngest.SourceGeneration.Steps;

// The code that runs in every call of a function, outside its steps. A function body is a method, a local function or
// a lambda that takes FunctionContext<TEventData>: ExecuteAsync and OnFailureAsync of a class, the handler of a
// delegate function, and a helper that the function hands its context to. Middleware takes the non-generic
// FunctionContext, and runs in every call by design, so it is left out.
//
// Inside a body, the code that runs where it is written counts: lambdas passed to a call, such as the callback of
// ParallelAsync or a LINQ selector, but not the handlers of steps, which run inside the step, and not local functions or
// lambdas kept in a variable, which run wherever they are called, often from inside a step.
internal readonly struct FunctionBody(IOperation operation, IParameterSymbol context)
{
    public IOperation Operation { get; } = operation;

    // The FunctionContext<TEventData> parameter, whose Step property the code fixes call.
    public IParameterSymbol Context { get; } = context;

    // The bodies in the operation blocks of a symbol: the symbol's own body when it takes a FunctionContext<T>, and
    // the lambdas and local functions in it that take one, such as the handlers of delegate functions in Program.cs. A
    // body inside another is walked on its own, and the walk of the outer one skips it.
    public static IEnumerable<FunctionBody> In(ISymbol owner, IEnumerable<IOperation> blocks)
    {
        var ownContext = owner is IMethodSymbol method ? ContextParameter(method) : null;
        foreach (var block in blocks)
        {
            if (ownContext is not null)
            {
                yield return new FunctionBody(block, ownContext);
            }

            foreach (var operation in block.Descendants())
            {
                var symbol = operation switch
                {
                    IAnonymousFunctionOperation function => function.Symbol,
                    ILocalFunctionOperation local => local.Symbol,
                    _ => null,
                };

                if (symbol is not null && ContextParameter(symbol) is { } context)
                {
                    yield return new FunctionBody(operation, context);
                }
            }
        }
    }

    public static IParameterSymbol? ContextParameter(IMethodSymbol method)
    {
        foreach (var parameter in method.Parameters)
        {
            if (InngestTypes.IsFunctionContextOfT(parameter.Type))
            {
                return parameter;
            }
        }

        return null;
    }

    // Whether a lambda or local function inside a body runs where it is written. A body nested in another, which is
    // rare, is walked on its own.
    public static bool RunsInPlace(IOperation operation)
    {
        if (operation is not IAnonymousFunctionOperation function
            || StepCalls.StepOfHandler(function) is not null
            || ContextParameter(function.Symbol) is not null)
        {
            return false;
        }

        var parent = function.Parent;
        while (parent is IDelegateCreationOperation or IConversionOperation)
        {
            parent = parent.Parent;
        }

        return parent is IArgumentOperation;
    }
}
