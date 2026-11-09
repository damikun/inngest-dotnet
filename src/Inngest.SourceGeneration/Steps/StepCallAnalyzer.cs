// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Inngest.SourceGeneration.Steps;

/// <summary>
/// Finds the calls of steps that break when Inngest replays the function: a step inside another step's handler, a
/// handler that assigns what the function reads later, an id that changes from call to call, a blocking wait on a step,
/// and a step or SendAsync whose task is dropped.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StepCallAnalyzer : DiagnosticAnalyzer
{
    // Set on INNGEST023 when the call is in an async function, where the code fix can await the task instead.
    internal const string CanAwaitProperty = "CanAwait";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        DiagnosticDescriptors.StateAssignedInStep,
        DiagnosticDescriptors.NondeterministicStepId,
        DiagnosticDescriptors.BlockingOnStep,
        DiagnosticDescriptors.NestedStep,
        DiagnosticDescriptors.StepNotAwaited);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;

        if (StepCalls.IsOnStepTools(method))
        {
            CheckDropped(context, invocation);
            CheckBlocking(context, invocation);
            if (method.Name == StepCalls.Parallel)
            {
                return;
            }

            CheckNested(context, invocation);
            CheckId(context, invocation);
            StepState.Check(context, invocation);
        }
        else if (StepCalls.IsSend(method))
        {
            CheckDropped(context, invocation);
        }
    }

    private static void CheckNested(OperationAnalysisContext context, IInvocationOperation invocation)
    {
        if (StepCalls.EnclosingStep(invocation) is { } outer)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.NestedStep,
                StepCalls.NameLocation(invocation),
                StepCalls.Describe(invocation),
                StepCalls.Describe(outer)));
        }
    }

    private static void CheckId(OperationAnalysisContext context, IInvocationOperation invocation)
    {
        if (StepCalls.IdArgument(invocation) is { } argument && Nondeterminism.Find(argument.Value) is { } source)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.NondeterministicStepId,
                argument.Syntax.GetLocation(),
                StepCalls.Describe(invocation),
                source));
        }
    }

    // .Result, .Wait() and .GetAwaiter().GetResult() on the task a step returns, with or without ConfigureAwait.
    private static void CheckBlocking(OperationAnalysisContext context, IInvocationOperation invocation)
    {
        IOperation task = invocation;
        if (task.Parent is IInvocationOperation { TargetMethod.Name: "ConfigureAwait" } configured && configured.Instance == task)
        {
            task = configured;
        }

        IOperation? blocking = task.Parent switch
        {
            IPropertyReferenceOperation { Property.Name: "Result" } result when result.Instance == task => result,
            IInvocationOperation { TargetMethod.Name: "Wait" } wait when wait.Instance == task && task == invocation => wait,
            IInvocationOperation { TargetMethod.Name: "GetAwaiter", Parent: IInvocationOperation { TargetMethod.Name: "GetResult" } getResult } awaiter
                when awaiter.Instance == task => getResult,
            _ => null,
        };

        if (blocking is null)
        {
            return;
        }

        var name = blocking switch
        {
            IPropertyReferenceOperation => ".Result",
            IInvocationOperation { TargetMethod.Name: "Wait" } => ".Wait()",
            _ => ".GetAwaiter().GetResult()",
        };

        var properties = StepCalls.CanAwait(blocking)
            ? ImmutableDictionary<string, string?>.Empty.Add(CanAwaitProperty, "true")
            : ImmutableDictionary<string, string?>.Empty;

        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.BlockingOnStep,
            blocking.Syntax.GetLocation(),
            properties,
            name,
            StepCalls.Describe(invocation)));
    }

    // A call whose task is the value of an expression statement, in a function that is not async. In an async one the
    // compiler reports it already (CS4014). Assigning the task to a discard says that nothing should wait for it.
    private static void CheckDropped(OperationAnalysisContext context, IInvocationOperation invocation)
    {
        if (invocation.Parent is not IExpressionStatementOperation || StepCalls.IsInAsyncFunction(invocation))
        {
            return;
        }

        var description = StepCalls.IsSend(invocation.TargetMethod) ? "SendAsync" : StepCalls.Describe(invocation);
        context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.StepNotAwaited, StepCalls.NameLocation(invocation), description));
    }
}
