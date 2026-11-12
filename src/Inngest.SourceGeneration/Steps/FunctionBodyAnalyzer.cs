// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Inngest.SourceGeneration.Steps;

/// <summary>
/// Looks at the code of a function that runs in every call, outside its steps (see <see cref="FunctionBody"/>): values
/// from the clock or a random source, waits with Task.Delay or Thread.Sleep, awaited calls that are not steps, loops
/// over unordered collections that call steps, and two steps with the same id.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class FunctionBodyAnalyzer : DiagnosticAnalyzer
{
    // The name of the FunctionContext<T> parameter, which the code fixes call Step on, set when the fix can await.
    internal const string ContextProperty = "Context";

    // The id the code fix of INNGEST026 gives the step, before it makes it unique in the method.
    internal const string StepIdProperty = "StepId";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        DiagnosticDescriptors.NondeterministicValue,
        DiagnosticDescriptors.DelayInFunction,
        DiagnosticDescriptors.AwaitOutsideStep,
        DiagnosticDescriptors.UnorderedLoopOverSteps,
        DiagnosticDescriptors.RepeatedStepId);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationBlockAction(AnalyzeBlocks);
    }

    private static void AnalyzeBlocks(OperationBlockAnalysisContext context)
    {
        foreach (var body in FunctionBody.In(context.OwningSymbol, context.OperationBlocks))
        {
            var walker = new Walker(context, body);
            walker.Walk(body.Operation, quiet: false);
            walker.ReportRepeatedIds();
        }
    }

    private sealed class Walker(OperationBlockAnalysisContext context, FunctionBody body)
    {
        private readonly List<(string Id, IInvocationOperation Step, IArgumentOperation Argument)> _ids = [];
        private readonly HashSet<IForEachLoopOperation> _reportedLoops = [];

        // Quiet is set inside the arguments of a step, which are saved with the step on its first call and not read
        // again, inside the arguments of a log message, which records the value without acting on it, and inside a
        // throw, which ends the call, as a RetryAfterException with a time from the clock does.
        public void Walk(IOperation operation, bool quiet)
        {
            if (operation != body.Operation && operation is IAnonymousFunctionOperation or ILocalFunctionOperation && !FunctionBody.RunsInPlace(operation))
            {
                return;
            }

            Visit(operation, quiet);

            var quietChildren = quiet
                || operation is IThrowOperation
                || (operation is IInvocationOperation invocation && (StepCalls.IsStep(invocation.TargetMethod) || IsLogging(invocation.TargetMethod)));
            foreach (var child in operation.ChildOperations)
            {
                Walk(child, quietChildren);
            }
        }

        private void Visit(IOperation operation, bool quiet)
        {
            switch (operation)
            {
                case IInvocationOperation invocation when StepCalls.IsStep(invocation.TargetMethod):
                    CollectId(invocation);
                    CheckLoops(invocation);
                    break;
                case IInvocationOperation invocation when IsDelay(invocation.TargetMethod):
                    if (!quiet)
                    {
                        ReportDelay(invocation);
                    }

                    return;
                case IAwaitOperation awaitOperation:
                    CheckAwait(awaitOperation);
                    break;
            }

            if (!quiet && Nondeterminism.DescribeValue(operation) is { } value)
            {
                ReportValue(operation, value);
            }
        }

        // INNGEST026. A Random is reported where a method is called on it, so that the fix takes the number into the
        // step and not the instance.
        private void ReportValue(IOperation operation, string value)
        {
            var reported = operation;
            if (operation is IPropertyReferenceOperation or IObjectCreationOperation
                && operation.Parent is IInvocationOperation call && call.Instance == operation)
            {
                reported = call;
                value = $"{value}.{call.TargetMethod.Name}()";
            }

            var properties = ImmutableDictionary<string, string?>.Empty;
            if (IsValue(reported.Type) && CanAwait(reported))
            {
                properties = properties.Add(ContextProperty, body.Context.Name).Add(StepIdProperty, StepIdFor(value));
            }

            context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.NondeterministicValue, reported.Syntax.GetLocation(), properties, value));
        }

        // INNGEST027. The fix replaces an awaited Task.Delay, or a Thread.Sleep that is a statement of its own.
        private void ReportDelay(IInvocationOperation invocation)
        {
            var name = invocation.TargetMethod.ContainingType.Name + "." + invocation.TargetMethod.Name;
            var replaceable = invocation.TargetMethod.Name == "Sleep"
                ? invocation.Parent is IExpressionStatementOperation
                : Unconfigured(invocation.Parent, invocation) is IAwaitOperation;
            var properties = replaceable && CanAwait(invocation)
                ? ImmutableDictionary<string, string?>.Empty.Add(ContextProperty, body.Context.Name)
                : ImmutableDictionary<string, string?>.Empty;

            context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.DelayInFunction, StepCalls.NameLocation(invocation), properties, name));
        }

        // INNGEST028: an awaited call that is neither a step nor on the short list of calls that are safe to repeat or
        // can run steps themselves. Task.WhenAll and Task.WhenAny are looked into, for the calls they are given.
        private void CheckAwait(IAwaitOperation awaitOperation)
        {
            if (Awaited(awaitOperation.Operation) is not { } invocation)
            {
                return;
            }

            if (IsWhenAllOrAny(invocation.TargetMethod))
            {
                foreach (var argument in invocation.Arguments)
                {
                    foreach (var element in Elements(argument.Value))
                    {
                        if (Awaited(element) is { } inner && !IsSafeToAwait(inner))
                        {
                            ReportAwait(inner);
                        }
                    }
                }

                return;
            }

            if (!IsSafeToAwait(invocation))
            {
                ReportAwait(invocation);
            }
        }

        private void ReportAwait(IInvocationOperation invocation)
        {
            var method = invocation.TargetMethod;
            var name = method.ReducedFrom is not null || method.IsExtensionMethod ? method.Name : method.ContainingType.Name + "." + method.Name;
            context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.AwaitOutsideStep, StepCalls.NameLocation(invocation), name));
        }

        // INNGEST032 collects the constant ids here and reports them when the walk is done.
        private void CollectId(IInvocationOperation step)
        {
            if (StepCalls.IdArgument(step) is { } argument && ConstantId(argument.Value) is { } id)
            {
                _ids.Add((id, step, argument));
            }
        }

        public void ReportRepeatedIds()
        {
            for (var i = 1; i < _ids.Count; i++)
            {
                for (var j = 0; j < i; j++)
                {
                    var (id, step, argument) = _ids[i];
                    var first = _ids[j];
                    if (first.Id != id || first.Step == step || Branches.AreExclusive(first.Step.Syntax, step.Syntax))
                    {
                        continue;
                    }

                    var line = first.Step.Syntax.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    context.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.RepeatedStepId,
                        argument.Value.Syntax.GetLocation(),
                        id,
                        first.Step.TargetMethod.Name,
                        line.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    break;
                }
            }
        }

        // INNGEST029: a step in a loop over an unordered collection whose id does not come from the item.
        private void CheckLoops(IInvocationOperation step)
        {
            IOperation previous = step;
            for (var current = step.Parent; current is not null && previous != body.Operation; previous = current, current = current.Parent)
            {
                if (current is not IForEachLoopOperation loop || previous != loop.Body || _reportedLoops.Contains(loop)
                    || UnorderedCollection(loop) is not { } collection)
                {
                    continue;
                }

                if (StepCalls.IdArgument(step) is { } argument && !UsesLoopValue(argument.Value, loop))
                {
                    _reportedLoops.Add(loop);
                    var location = loop.Syntax is ForEachStatementSyntax statement ? statement.Expression.GetLocation() : loop.Collection.Syntax.GetLocation();
                    context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.UnorderedLoopOverSteps, location, collection, StepCalls.Describe(step)));
                }
            }
        }

        // The id reads the item, or a local declared inside the loop, which is made from the item in all but odd code.
        private static bool UsesLoopValue(IOperation id, IForEachLoopOperation loop)
        {
            foreach (var operation in id.DescendantsAndSelf())
            {
                if (operation is ILocalReferenceOperation reference && IsDeclaredIn(reference.Local, loop.Syntax))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsDeclaredIn(ILocalSymbol local, SyntaxNode node)
        {
            foreach (var declaration in local.DeclaringSyntaxReferences)
            {
                if (declaration.SyntaxTree == node.SyntaxTree && node.Span.Contains(declaration.Span))
                {
                    return true;
                }
            }

            return false;
        }

        // The id the fix gives a step that reads the value, from what is read.
        private static string StepIdFor(string value) => value switch
        {
            _ when value.StartsWith("Guid.", System.StringComparison.Ordinal) => "new-guid",
            _ when value.Contains("Random") => "random-number",
            _ when value.Contains("UtcNow") => "utc-now",
            _ when value.Contains("Today") => "today",
            _ when value.Contains("Now") => "now",
            _ when value.Contains("TickCount") => "tick-count",
            _ when value.Contains("Timestamp") => "timestamp",
            _ => "value",
        };

        private bool CanAwait(IOperation operation) =>
            body.Context.Name.Length > 0 && body.Context.Name != "_" && StepCalls.CanAwait(operation) && !IsInFilterOrQuery(operation);

        private static bool IsInFilterOrQuery(IOperation operation)
        {
            for (var node = operation.Syntax.Parent; node is not null; node = node.Parent)
            {
                if (node is CatchFilterClauseSyntax or QueryExpressionSyntax)
                {
                    return true;
                }

                if (node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax or MemberDeclarationSyntax)
                {
                    return false;
                }
            }

            return false;
        }
    }

    // What the code fix of INNGEST026 can save as a step's result: the values the reported calls return.
    private static bool IsValue(ITypeSymbol? type) => type is not null && (type.SpecialType is
        SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_Double or SpecialType.System_Single
        or SpecialType.System_DateTime or SpecialType.System_String or SpecialType.System_Boolean
        || type is { Name: "Guid" or "DateTimeOffset" or "TimeSpan", ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } });

    private static bool IsDelay(IMethodSymbol method) =>
        (method.Name == "Delay" && IsType(method.ContainingType, "Task", "System", "Threading", "Tasks"))
        || (method.Name == "Sleep" && IsType(method.ContainingType, "Thread", "System", "Threading"));

    private static bool IsWhenAllOrAny(IMethodSymbol method) =>
        method.Name is "WhenAll" or "WhenAny" && IsType(method.ContainingType, "Task", "System", "Threading", "Tasks");

    // A log message's arguments, from ILogger's extension methods or a method that takes a logger, as the methods
    // [LoggerMessage] generates do.
    private static bool IsLogging(IMethodSymbol method)
    {
        if (method.ContainingNamespace?.ToDisplayString() == "Microsoft.Extensions.Logging")
        {
            return true;
        }

        foreach (var parameter in (method.ReducedFrom ?? method).Parameters)
        {
            if (parameter.Type is { Name: "ILogger" } && parameter.Type.ContainingNamespace?.ToDisplayString() == "Microsoft.Extensions.Logging")
            {
                return true;
            }
        }

        return false;
    }

    // The calls that are fine to await outside a step:
    // - the steps themselves, the SDK's other calls, which take care of replays, and SendAsync of the client is not one
    //   of them, since it sends the events again in every call;
    // - a call that is given the function's context or its step tools, which can run steps;
    // - local functions and delegates, whose code is not known here;
    // - System.Threading, whose calls coordinate tasks and do no I/O of their own;
    // - reading the content of an HTTP response the function already has, where the request is what counts;
    // - Microsoft.Extensions.AI and Microsoft.Agents.AI, whose calls UseInngestSteps turns into steps.
    private static bool IsSafeToAwait(IInvocationOperation invocation)
    {
        var method = invocation.TargetMethod;
        if (StepCalls.IsOnStepTools(method) || IsDelay(method))
        {
            return true;
        }

        if (StepCalls.IsSend(method))
        {
            return false;
        }

        if (method.MethodKind is MethodKind.LocalFunction or MethodKind.DelegateInvoke or MethodKind.AnonymousFunction
            || IsSdkAssembly((method.ReducedFrom ?? method).ContainingAssembly))
        {
            return true;
        }

        var space = (method.ReducedFrom ?? method).ContainingNamespace?.ToDisplayString() ?? string.Empty;
        if (IsIn(space, "System.Threading") || IsIn(space, "Microsoft.Extensions.AI") || IsIn(space, "Microsoft.Agents.AI"))
        {
            return true;
        }

        if (invocation.Instance is { } instance && (CanRunSteps(instance.Type) || IsHttpContent(instance.Type)))
        {
            return true;
        }

        if ((method.ReducedFrom ?? method) is { IsExtensionMethod: true, Parameters.Length: > 0 } extension && IsHttpContent(extension.Parameters[0].Type))
        {
            return true;
        }

        foreach (var argument in invocation.Arguments)
        {
            if (CanRunSteps(Unconverted(argument.Value).Type))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsIn(string space, string root) =>
        space == root || space.StartsWith(root + ".", System.StringComparison.Ordinal);

    private static bool IsSdkAssembly(IAssemblySymbol? assembly) =>
        assembly is not null && (assembly.Name == "Inngest" || assembly.Name.StartsWith("Inngest.", System.StringComparison.Ordinal));

    private static bool IsHttpContent(ITypeSymbol? type)
    {
        for (var current = type as INamedTypeSymbol; current is not null; current = current.BaseType)
        {
            if (IsType(current, "HttpContent", "System", "Net", "Http"))
            {
                return true;
            }
        }

        return false;
    }

    // A function context or step tools, or a type that holds one, such as the context of a failure handler.
    private static bool CanRunSteps(ITypeSymbol? type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current is INamedTypeSymbol { Name: "FunctionContext" } named && IsSdkAssembly(named.ContainingAssembly))
            {
                return true;
            }
        }

        if (InngestTypes.IsStepToolsInterface(type))
        {
            return true;
        }

        foreach (var @interface in type?.AllInterfaces ?? ImmutableArray<INamedTypeSymbol>.Empty)
        {
            if (InngestTypes.IsStepToolsInterface(@interface))
            {
                return true;
            }
        }

        return false;
    }

    // The call whose task is awaited, through ConfigureAwait and conversions.
    private static IInvocationOperation? Awaited(IOperation operation)
    {
        operation = Unconverted(operation);
        if (operation is IInvocationOperation { TargetMethod.Name: "ConfigureAwait", Instance: { } configured })
        {
            operation = Unconverted(configured);
        }

        return operation as IInvocationOperation;
    }

    // The operation that takes the task of a call, past a ConfigureAwait.
    private static IOperation? Unconfigured(IOperation? parent, IOperation task)
    {
        while (parent is IConversionOperation)
        {
            parent = parent.Parent;
        }

        return parent is IInvocationOperation { TargetMethod.Name: "ConfigureAwait" } configured && configured.Instance == task ? configured.Parent : parent;
    }

    private static IOperation Unconverted(IOperation operation)
    {
        while (operation is IConversionOperation conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
    }

    // The tasks passed to WhenAll or WhenAny one by one, as a params array. A collection of tasks made elsewhere, such
    // as by a Select whose lambda this walk looks into anyway, is not one of them.
    private static IEnumerable<IOperation> Elements(IOperation value)
    {
        value = Unconverted(value);
        if (value is IArrayCreationOperation { Initializer: { } initializer })
        {
            foreach (var element in initializer.ElementValues)
            {
                yield return element;
            }
        }
        else if (IsTask(value.Type))
        {
            yield return value;
        }
        else if (value.Type is INamedTypeSymbol { Name: "ReadOnlySpan" })
        {
            // A params ReadOnlySpan<Task>, which .NET 9 added and newer compilers build as a collection expression, a
            // kind of operation this assembly's version of Roslyn has no interface for. Its children are the tasks.
            foreach (var element in value.ChildOperations)
            {
                if (IsTask(Unconverted(element).Type) || IsTask(element.Type))
                {
                    yield return element;
                }
            }
        }
    }

    private static bool IsTask(ITypeSymbol? type) =>
        type is INamedTypeSymbol { Name: "Task" or "ValueTask" } named && IsType(named, named.Name, "System", "Threading", "Tasks");

    private static string? ConstantId(IOperation value)
    {
        value = Unconverted(value);
        if (value.ConstantValue is { HasValue: true, Value: string id })
        {
            return id;
        }

        // new StepOptions("id") or new StepOptions("id") { Name = ... }.
        return value is IObjectCreationOperation creation && InngestTypes.IsStepOptions(creation.Type) && creation.Arguments.Length > 0
            && creation.Arguments[0].Value.ConstantValue is { HasValue: true, Value: string optionsId }
                ? optionsId
                : null;
    }

    // Dictionary, HashSet and their concurrent and immutable kin, and the keys and values of a dictionary.
    private static string? UnorderedCollection(IForEachLoopOperation loop)
    {
        var type = Unconverted(loop.Collection).Type as INamedTypeSymbol;
        if (type is { Name: "KeyCollection" or "ValueCollection", ContainingType: { } dictionary })
        {
            type = dictionary;
        }

        if (type?.OriginalDefinition is not { } definition)
        {
            return null;
        }

        var space = definition.ContainingNamespace?.ToDisplayString();
        return (definition.Name, space) switch
        {
            ("Dictionary" or "HashSet", "System.Collections.Generic") => definition.Name,
            ("ConcurrentDictionary" or "ConcurrentBag", "System.Collections.Concurrent") => definition.Name,
            ("ImmutableDictionary" or "ImmutableHashSet", "System.Collections.Immutable") => definition.Name,
            _ => null,
        };
    }

    private static bool IsType(INamedTypeSymbol? type, string name, params string[] namespaces)
    {
        if (type?.Name != name)
        {
            return false;
        }

        var current = type.ContainingNamespace;
        for (var i = namespaces.Length - 1; i >= 0; i--)
        {
            if (current is null || current.Name != namespaces[i])
            {
                return false;
            }

            current = current.ContainingNamespace;
        }

        return current is { IsGlobalNamespace: true };
    }
}
