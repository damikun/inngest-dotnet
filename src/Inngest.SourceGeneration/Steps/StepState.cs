// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Inngest.SourceGeneration.Steps;

// INNGEST020: a step's handler assigns a local, a parameter or a field, and the function that calls the step reads it
// outside the handler. Locals and parameters come from the compiler's data flow analysis of the lambda: written inside
// and read outside. Fields are those of the class itself (this.x or a static field). Only reads in the body of the
// function count, that is the method, local function or lambda that calls the step, with the callbacks of
// ParallelAsync in it: code that reads the variable after the function has returned, such as a test, sees the value of
// the call that ran the handler. Changes made through a reference, such as list.Add, are not assignments.
internal static class StepState
{
    public static void Check(OperationAnalysisContext context, IInvocationOperation invocation)
    {
        foreach (var handler in StepCalls.Handlers(invocation))
        {
            CheckHandler(context, invocation, handler);
        }
    }

    private static void CheckHandler(OperationAnalysisContext context, IInvocationOperation step, IAnonymousFunctionOperation handler)
    {
        if (handler.Syntax is not AnonymousFunctionExpressionSyntax lambda || handler.SemanticModel is not { } model)
        {
            return;
        }

        var body = FunctionBody(step);
        var reported = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var flow = model.AnalyzeDataFlow(lambda);
        if (flow is { Succeeded: true })
        {
            foreach (var symbol in flow.WrittenInside)
            {
                if (symbol is not (ILocalSymbol or IParameterSymbol { IsThis: false })
                    || flow.VariablesDeclared.Contains(symbol)
                    || !flow.ReadOutside.Contains(symbol)
                    || !IsReadOutside(body, symbol, lambda.Span)
                    || FirstWrite(handler, symbol) is not { } write
                    || !reported.Add(symbol))
                {
                    continue;
                }

                Report(context, step, symbol, write);
            }
        }

        foreach (var write in FieldWrites(handler))
        {
            var field = write.Field;
            if (!reported.Contains(field) && IsReadOutside(body, field, lambda.Span) && reported.Add(field))
            {
                Report(context, step, field, write);
            }
        }
    }

    private static void Report(OperationAnalysisContext context, IInvocationOperation step, ISymbol symbol, IOperation write) =>
        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.StateAssignedInStep,
            write.Syntax.GetLocation(),
            symbol.Name,
            StepCalls.Describe(step)));

    private static IOperation? FirstWrite(IAnonymousFunctionOperation handler, ISymbol symbol)
    {
        foreach (var operation in handler.Body.Descendants())
        {
            var target = operation switch
            {
                ILocalReferenceOperation local => local.Local,
                IParameterReferenceOperation parameter => (ISymbol)parameter.Parameter,
                _ => null,
            };

            if (target is not null && SymbolEqualityComparer.Default.Equals(target, symbol) && IsWrite(operation))
            {
                return operation;
            }
        }

        return null;
    }

    private static IEnumerable<IFieldReferenceOperation> FieldWrites(IAnonymousFunctionOperation handler)
    {
        foreach (var operation in handler.Body.Descendants())
        {
            if (operation is IFieldReferenceOperation { Field.IsConst: false } field
                && (field.Field.IsStatic || field.Instance is IInstanceReferenceOperation)
                && IsWrite(field))
            {
                yield return field;
            }
        }
    }

    // Any read of the variable in the function's body, outside the handler. A compound assignment or an increment
    // reads it too.
    private static bool IsReadOutside(IOperation body, ISymbol symbol, Microsoft.CodeAnalysis.Text.TextSpan handler)
    {
        foreach (var operation in body.Descendants())
        {
            var target = operation switch
            {
                ILocalReferenceOperation local => local.Local,
                IParameterReferenceOperation parameter => parameter.Parameter,
                IFieldReferenceOperation field => (ISymbol)field.Field,
                _ => null,
            };

            if (target is not null
                && SymbolEqualityComparer.Default.Equals(target, symbol)
                && !handler.Contains(operation.Syntax.Span)
                && !IsOnlyWrite(operation))
            {
                return true;
            }
        }

        return false;
    }

    // The method, local function or lambda that calls the step. The callback of ParallelAsync runs as part of the
    // function around it, so it belongs to that function.
    private static IOperation FunctionBody(IOperation operation)
    {
        var current = operation;
        while (current.Parent is { } parent)
        {
            if (parent is ILocalFunctionOperation
                || (parent is IAnonymousFunctionOperation function && !IsParallelCallback(function)))
            {
                return parent;
            }

            current = parent;
        }

        return current;
    }

    private static bool IsParallelCallback(IAnonymousFunctionOperation function)
    {
        var operation = function.Parent;
        while (operation is IDelegateCreationOperation or IConversionOperation)
        {
            operation = operation.Parent;
        }

        return operation is IArgumentOperation { Parent: IInvocationOperation invocation }
            && invocation.TargetMethod.Name == StepCalls.Parallel
            && StepCalls.IsOnStepTools(invocation.TargetMethod);
    }

    private static bool IsWrite(IOperation reference)
    {
        var parent = reference.Parent;
        while (parent is ITupleOperation)
        {
            reference = parent;
            parent = parent.Parent;
        }

        return parent switch
        {
            IAssignmentOperation assignment => assignment.Target == reference,
            IIncrementOrDecrementOperation => true,
            IArgumentOperation argument => argument.Parameter?.RefKind is RefKind.Ref or RefKind.Out,
            _ => false,
        };
    }

    private static bool IsOnlyWrite(IOperation reference) =>
        reference.Parent is ISimpleAssignmentOperation assignment && assignment.Target == reference
        || reference.Parent is IArgumentOperation { Parameter.RefKind: RefKind.Out };
}
