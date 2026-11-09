// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Inngest.SourceGeneration.Steps;

// What the replay-safety analyzers know about the step tools: which calls are steps, which of their arguments is the
// id, and which delegates run as the body of a step. A step is any method of IStepTools, or an extension method on it,
// such as RunAsync, SleepAsync or MapAsync. ParallelAsync is not a step: it groups the steps its callback calls, and
// that callback runs as part of the function.
internal static class StepCalls
{
    public const string Parallel = "ParallelAsync";

    public static bool IsStep(IMethodSymbol method) => IsOnStepTools(method) && method.Name != Parallel;

    // A step or ParallelAsync, which a function awaits all the same.
    public static bool IsOnStepTools(IMethodSymbol method)
    {
        var definition = method.ReducedFrom ?? method;
        if (InngestTypes.IsStepToolsInterface(definition.ContainingType))
        {
            return true;
        }

        return definition.IsExtensionMethod
            && definition.Parameters.Length > 0
            && InngestTypes.IsStepToolsInterface(definition.Parameters[0].Type);
    }

    // SendAsync of IInngestClient and its extension methods.
    public static bool IsSend(IMethodSymbol method)
    {
        if (method.Name != "SendAsync")
        {
            return false;
        }

        var definition = method.ReducedFrom ?? method;
        return InngestTypes.IsClientInterface(definition.ContainingType)
            || (definition.IsExtensionMethod && definition.Parameters.Length > 0 && InngestTypes.IsClientInterface(definition.Parameters[0].Type));
    }

    // The id argument: a string named id, or the StepOptions that carry it.
    public static IArgumentOperation? IdArgument(IInvocationOperation invocation)
    {
        foreach (var argument in invocation.Arguments)
        {
            if (argument.Parameter is not { } parameter || argument.ArgumentKind == ArgumentKind.DefaultValue)
            {
                continue;
            }

            if ((parameter.Name == "id" && parameter.Type.SpecialType == SpecialType.System_String)
                || (parameter.Name == "stepOptions" && InngestTypes.IsStepOptions(parameter.Type)))
            {
                return argument;
            }
        }

        return null;
    }

    // Whether the lambda is the handler of a step, so that its body runs inside the step: the handler of RunAsync,
    // RunScopedAsync and AIWrapAsync, and the per-item handler of MapAsync and its siblings, which run each item as a
    // step. Returns the step call.
    public static IInvocationOperation? StepOfHandler(IAnonymousFunctionOperation function)
    {
        var operation = function.Parent;
        if (operation is IDelegateCreationOperation)
        {
            operation = operation.Parent;
        }

        if (operation is IConversionOperation)
        {
            operation = operation.Parent;
        }

        return operation is IArgumentOperation { Parent: IInvocationOperation invocation, Parameter.Type.TypeKind: TypeKind.Delegate }
            && IsStep(invocation.TargetMethod)
                ? invocation
                : null;
    }

    // The innermost step whose handler the operation is in, if any.
    public static IInvocationOperation? EnclosingStep(IOperation operation)
    {
        for (var current = operation.Parent; current is not null; current = current.Parent)
        {
            if (current is IAnonymousFunctionOperation function && StepOfHandler(function) is { } step)
            {
                return step;
            }
        }

        return null;
    }

    // The anonymous functions passed as handlers of the step.
    public static IEnumerable<IAnonymousFunctionOperation> Handlers(IInvocationOperation invocation)
    {
        foreach (var argument in invocation.Arguments)
        {
            if (argument.Parameter?.Type.TypeKind != TypeKind.Delegate)
            {
                continue;
            }

            var value = argument.Value;
            while (value is IConversionOperation or IDelegateCreationOperation)
            {
                value = value is IConversionOperation conversion ? conversion.Operand : ((IDelegateCreationOperation)value).Target;
            }

            if (value is IAnonymousFunctionOperation function)
            {
                yield return function;
            }
        }
    }

    // The id the code gave the step, when it is a constant, for the messages.
    public static string Describe(IInvocationOperation invocation)
    {
        if (IdArgument(invocation)?.Value is { ConstantValue: { HasValue: true, Value: string id } })
        {
            return $"{invocation.TargetMethod.Name}(\"{id}\")";
        }

        return invocation.TargetMethod.Name;
    }

    // The method name of the call, so that a squiggle does not cover the lambda that follows it.
    public static Location NameLocation(IInvocationOperation invocation)
    {
        if (invocation.Syntax is not InvocationExpressionSyntax { Expression: var expression })
        {
            return invocation.Syntax.GetLocation();
        }

        var name = expression switch
        {
            MemberAccessExpressionSyntax member => member.Name,
            MemberBindingExpressionSyntax binding => binding.Name,
            _ => expression,
        };

        return name is GenericNameSyntax generic ? generic.Identifier.GetLocation() : name.GetLocation();
    }

    // Whether code in the function that holds the operation may use await: the method, local function or lambda is
    // async and the operation is not inside a lock.
    public static bool CanAwait(IOperation operation)
    {
        for (var current = operation.Parent; current is not null; current = current.Parent)
        {
            if (current is ILockOperation)
            {
                return false;
            }

            if (current is IAnonymousFunctionOperation or ILocalFunctionOperation)
            {
                break;
            }
        }

        return IsInAsyncFunction(operation);
    }

    public static bool IsInAsyncFunction(IOperation operation)
    {
        for (var current = operation.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case IAnonymousFunctionOperation function:
                    return function.Symbol.IsAsync;
                case ILocalFunctionOperation local:
                    return local.Symbol.IsAsync;
            }
        }

        return operation.SemanticModel?.GetEnclosingSymbol(operation.Syntax.SpanStart) is IMethodSymbol { IsAsync: true };
    }
}
