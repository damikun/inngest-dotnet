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
/// over unordered collections that call steps, two steps with the same id, and blocking waits on tasks the code starts.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class FunctionBodyAnalyzer : DiagnosticAnalyzer
{
    // The name of the FunctionContext<T> parameter, which the code fixes call Step on, set when the fix can await.
    internal const string ContextProperty = "Context";

    // The id the code fix of INNGEST026 gives the step, before it makes it unique in the method.
    internal const string StepIdProperty = "StepId";

    // Set to TaskKind on an INNGEST023 about a task that is not a step's, which shares its id with the one about a step.
    internal const string KindProperty = "Kind";
    internal const string TaskKind = "Task";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        DiagnosticDescriptors.NondeterministicValue,
        DiagnosticDescriptors.DelayInFunction,
        DiagnosticDescriptors.AwaitOutsideStep,
        DiagnosticDescriptors.UnorderedLoopOverSteps,
        DiagnosticDescriptors.RepeatedStepId,
        DiagnosticDescriptors.BlockingOnStep,
        DiagnosticDescriptors.BlockingOnTask);

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

        // For INNGEST023, made on first use: the tasks in locals, by what the locals were declared with, the tasks that
        // the code puts in a collection in a local, and where the code first awaits a local's task or checks whether it
        // is done.
        private Dictionary<ILocalSymbol, IOperation>? _initializers;
        private Dictionary<ILocalSymbol, List<IOperation>>? _members;
        private Dictionary<ILocalSymbol, int>? _settled;

        // Quiet is set inside the arguments of a step, which are saved with the step on its first call and not read
        // again, inside the arguments of a log message, which records the value without acting on it, and inside a
        // throw, which ends the call, as a RetryAfterException with a time from the clock does. Off the thread is set
        // inside lambdas that System.Threading runs elsewhere, such as the work of Task.Run.
        public void Walk(IOperation operation, bool quiet, bool offThread = false)
        {
            if (operation != body.Operation && operation is IAnonymousFunctionOperation or ILocalFunctionOperation && !FunctionBody.RunsInPlace(operation))
            {
                return;
            }

            offThread = offThread || (operation != body.Operation && RunsOffThread(operation));
            Visit(operation, quiet);
            if (!offThread)
            {
                CheckBlocking(operation);
            }

            var quietChildren = quiet
                || operation is IThrowOperation
                || (operation is IInvocationOperation invocation && (StepCalls.IsStep(invocation.TargetMethod) || IsLogging(invocation.TargetMethod)));
            foreach (var child in operation.ChildOperations)
            {
                Walk(child, quietChildren, offThread);
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

        // INNGEST023: .Result, .Wait(), .GetAwaiter().GetResult(), Task.WaitAll or Task.WaitAny on a task that the code
        // starts itself, by calling a method, and may not have finished. A task in a parameter, a field or a property is
        // left alone, since it may come from anywhere, and so is one the code awaited or checked before. StepCallAnalyzer
        // reports a wait on a step call itself; a step's task kept in a local, or given to WaitAll, is reported here.
        private void CheckBlocking(IOperation operation)
        {
            var (task, how) = operation switch
            {
                IPropertyReferenceOperation { Property.Name: "Result", Instance: { } instance } when IsTask(instance.Type) => (instance, ".Result"),
                IInvocationOperation { TargetMethod.Name: "Wait", Instance: { } instance } when IsTask(instance.Type) => (instance, ".Wait()"),
                IInvocationOperation { TargetMethod.Name: "GetResult", Instance: IInvocationOperation { TargetMethod.Name: "GetAwaiter", Instance: { } instance } }
                    when IsAwaitable(instance.Type) => (instance, ".GetAwaiter().GetResult()"),
                IInvocationOperation { Instance: null } wait when IsWaitAllOrAny(wait.TargetMethod) => (wait, "Task." + wait.TargetMethod.Name + "()"),
                _ => (null, null),
            };

            if (task is null || StartedBy(task, operation, depth: 0) is not { } call)
            {
                return;
            }

            // StepCallAnalyzer reports the step call itself, with or without ConfigureAwait.
            var step = StepCalls.IsOnStepTools(call.TargetMethod);
            if (step && Unwrapped(task, throughWaits: false) == call)
            {
                return;
            }

            // The code fix awaits the task of a blocking call, and WaitAll and WaitAny have no one task to await.
            var properties = ImmutableDictionary<string, string?>.Empty;
            if (task != operation && StepCalls.CanAwait(operation) && !IsInFilterOrQuery(operation))
            {
                properties = properties.Add(StepCallAnalyzer.CanAwaitProperty, "true");
            }

            if (!step)
            {
                properties = properties.Add(KindProperty, TaskKind);
            }

            context.ReportDiagnostic(step
                ? Diagnostic.Create(DiagnosticDescriptors.BlockingOnStep, operation.Syntax.GetLocation(), properties, how, StepCalls.Describe(call))
                : Diagnostic.Create(DiagnosticDescriptors.BlockingOnTask, operation.Syntax.GetLocation(), properties, how, Describe(call)));
        }

        // The call that started the task, when it is one the code starts here and may not have finished by the blocking
        // wait: a call in place, or a local declared with one that was not awaited or checked first. Task.WhenAll,
        // WhenAny, WaitAll and WaitAny are looked into, for the tasks they are given one by one, and ContinueWith for the
        // task it continues: the continuation runs on the thread pool once that task is done. Given a scheduler of its
        // own, such as the function's thread from FromCurrentSynchronizationContext, the continuation is a task the code
        // starts itself.
        private IInvocationOperation? StartedBy(IOperation task, IOperation blocking, int depth)
        {
            switch (Unwrapped(task))
            {
                case IInvocationOperation { TargetMethod.Name: "ContinueWith", Instance: { } continued } call when IsTask(call.TargetMethod.ContainingType):
                    return (depth < 4 ? StartedBy(continued, blocking, depth + 1) : null) ?? (HasOwnScheduler(call) ? call : null);
                case IInvocationOperation call when IsWhenAllOrAny(call.TargetMethod) || IsWaitAllOrAny(call.TargetMethod):
                    foreach (var argument in call.Arguments)
                    {
                        foreach (var element in Elements(argument.Value))
                        {
                            if (depth < 4 && StartedBy(element, blocking, depth + 1) is { } started)
                            {
                                return started;
                            }
                        }
                    }

                    return null;
                case IInvocationOperation call:
                    return StartsTask(call) ? call : null;
                case ILocalReferenceOperation reference when depth < 4 && !IsSettledBefore(reference.Local, blocking):
                    return Initializers().TryGetValue(reference.Local, out var initializer) ? StartedBy(initializer, blocking, depth + 1) : null;
                default:
                    return null;
            }
        }

        // A call whose task can still be running. The From methods of Task and ValueTask make tasks that are done, the
        // work of Task.Run and StartNew runs on the thread pool, without the function's thread, which makes blocking on
        // it the usual way out of an async API, and Task.Delay is INNGEST027's.
        private static bool StartsTask(IInvocationOperation call)
        {
            var method = call.TargetMethod;
            if (IsType(method.ContainingType, "Task", "System", "Threading", "Tasks") || IsType(method.ContainingType, "ValueTask", "System", "Threading", "Tasks"))
            {
                return !method.Name.StartsWith("From", System.StringComparison.Ordinal) && method.Name is not ("Run" or "Delay");
            }

            return !(method.Name == "StartNew" && IsType(method.ContainingType, "TaskFactory", "System", "Threading", "Tasks"));
        }

        // A ContinueWith given a scheduler other than the thread pool's. TaskScheduler.Current is the one ContinueWith
        // uses when it is given none, and in the code of a function that is the thread pool's too.
        private static bool HasOwnScheduler(IInvocationOperation continueWith)
        {
            foreach (var argument in continueWith.Arguments)
            {
                if (argument.Parameter?.Type is INamedTypeSymbol type && IsType(type, "TaskScheduler", "System", "Threading", "Tasks"))
                {
                    return Unconverted(argument.Value) is not IPropertyReferenceOperation { Property.Name: "Default" or "Current", Instance: null } property
                        || !IsType(property.Property.ContainingType, "TaskScheduler", "System", "Threading", "Tasks");
                }
            }

            return false;
        }

        private bool IsSettledBefore(ILocalSymbol local, IOperation blocking)
        {
            if (_settled is null)
            {
                FindLocals();
            }

            return _settled!.TryGetValue(local, out var position) && position < blocking.Syntax.SpanStart;
        }

        private Dictionary<ILocalSymbol, IOperation> Initializers()
        {
            if (_initializers is null)
            {
                FindLocals();
            }

            return _initializers!;
        }

        // A local is settled where the code awaits its task, alone or among those of Task.WhenAll or WhenAny, reads
        // whether it is done, as with IsCompleted, or blocks on it, which is reported there and not again. Awaiting a
        // local that holds a WhenAll settles the tasks it was given, and so does blocking on one, or Task.WaitAll. An
        // await or a check also settles the tasks in a collection in a local that WhenAll is given, from the collection's
        // initializer or an Add; a blocking wait does not, since it is reported only on the tasks it is given one by one,
        // and a read of the task after it still is. A wait through WaitAsync settles nothing, since WaitAsync can give up
        // while the task goes on.
        private void FindLocals()
        {
            _initializers = new Dictionary<ILocalSymbol, IOperation>(SymbolEqualityComparer.Default);
            _members = new Dictionary<ILocalSymbol, List<IOperation>>(SymbolEqualityComparer.Default);
            _settled = new Dictionary<ILocalSymbol, int>(SymbolEqualityComparer.Default);
            foreach (var operation in body.Operation.DescendantsAndSelf())
            {
                switch (operation)
                {
                    case IVariableDeclaratorOperation declarator when declarator.GetVariableInitializer() is { Value: { } value }:
                        _initializers[declarator.Symbol] = value;
                        foreach (var element in CollectionElements(value))
                        {
                            AddMember(declarator.Symbol, element);
                        }

                        break;
                    case IInvocationOperation { TargetMethod.Name: "Add", Instance: ILocalReferenceOperation collection, Arguments.Length: 1 } add:
                        AddMember(collection.Local, add.Arguments[0].Value);
                        break;
                }
            }

            foreach (var operation in body.Operation.DescendantsAndSelf())
            {
                switch (operation)
                {
                    case IAwaitOperation awaitOperation:
                        Settle(awaitOperation.Operation, operation, throughCollections: true, depth: 0);
                        break;
                    case IPropertyReferenceOperation { Property.Name: "IsCompleted" or "IsCompletedSuccessfully" or "IsFaulted" or "IsCanceled" or "Status", Instance: { } instance }
                        when IsTask(instance.Type):
                        Settle(instance, operation, throughCollections: true, depth: 0);
                        break;
                    case IPropertyReferenceOperation { Property.Name: "Result", Instance: { } instance } when IsTask(instance.Type):
                        Settle(instance, operation, throughCollections: false, depth: 0);
                        break;
                    case IInvocationOperation { TargetMethod.Name: "Wait" or "GetAwaiter", Instance: { } instance } when IsAwaitable(instance.Type):
                        Settle(instance, operation, throughCollections: false, depth: 0);
                        break;
                    case IInvocationOperation { Instance: null } wait when IsWaitAllOrAny(wait.TargetMethod):
                        Settle(wait, operation, throughCollections: false, depth: 0);
                        break;
                }
            }
        }

        private void AddMember(ILocalSymbol collection, IOperation element)
        {
            if (!IsTask(Unconverted(element).Type))
            {
                return;
            }

            if (!_members!.TryGetValue(collection, out var members))
            {
                _members[collection] = members = [];
            }

            members.Add(element);
        }

        private void Settle(IOperation task, IOperation where, bool throughCollections, int depth)
        {
            task = Unwrapped(task, throughWaits: false);
            if (task is ILocalReferenceOperation reference)
            {
                if (!_settled!.TryGetValue(reference.Local, out var position) || where.Syntax.SpanStart < position)
                {
                    _settled[reference.Local] = where.Syntax.SpanStart;
                }

                if (!_initializers!.TryGetValue(reference.Local, out var initializer))
                {
                    return;
                }

                task = Unwrapped(initializer, throughWaits: false);
            }

            if (depth >= 4 || task is not IInvocationOperation call || !(IsWhenAllOrAny(call.TargetMethod) || IsWaitAllOrAny(call.TargetMethod)))
            {
                return;
            }

            foreach (var argument in call.Arguments)
            {
                foreach (var element in Elements(argument.Value))
                {
                    Settle(element, where, throughCollections, depth + 1);
                }

                if (throughCollections && Unconverted(argument.Value) is ILocalReferenceOperation collection && _members!.TryGetValue(collection.Local, out var members))
                {
                    foreach (var member in members)
                    {
                        Settle(member, where, throughCollections, depth + 1);
                    }
                }
            }
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

    private static bool IsWaitAllOrAny(IMethodSymbol method) =>
        method.Name is "WaitAll" or "WaitAny" && IsType(method.ContainingType, "Task", "System", "Threading", "Tasks");

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

    // What a local collection is declared with: an array, a collection initializer or a collection expression, which
    // this assembly's version of Roslyn knows only by its syntax.
    private static IEnumerable<IOperation> CollectionElements(IOperation value)
    {
        value = Unconverted(value);
        if (value is IArrayCreationOperation { Initializer: { } array })
        {
            return array.ElementValues;
        }

        if (value is IObjectCreationOperation { Initializer: { } initializer })
        {
            var elements = new List<IOperation>();
            foreach (var item in initializer.Initializers)
            {
                if (item is IInvocationOperation { Arguments.Length: 1 } add)
                {
                    elements.Add(add.Arguments[0].Value);
                }
            }

            return elements;
        }

        if (value.Syntax is CollectionExpressionSyntax)
        {
            return value.ChildOperations;
        }

        return [];
    }

    private static bool IsTask(ITypeSymbol? type) =>
        type is INamedTypeSymbol { Name: "Task" or "ValueTask" } named && IsType(named, named.Name, "System", "Threading", "Tasks");

    // The task itself, past conversions, ConfigureAwait, AsTask and, through waits, WaitAsync, whose task completes with
    // it or gives up waiting for it.
    private static IOperation Unwrapped(IOperation task, bool throughWaits = true)
    {
        task = Unconverted(task);
        while (task is IInvocationOperation { Instance: { } inner } call
            && (call.TargetMethod.Name is "ConfigureAwait" or "AsTask" || (throughWaits && call.TargetMethod.Name == "WaitAsync" && IsTask(inner.Type))))
        {
            task = Unconverted(inner);
        }

        return task;
    }

    // What GetAwaiter().GetResult() blocks on: a task, or a task after ConfigureAwait.
    private static bool IsAwaitable(ITypeSymbol? type) =>
        IsTask(type)
        || (type is INamedTypeSymbol { Name: "ConfiguredTaskAwaitable" or "ConfiguredValueTaskAwaitable" } named
            && IsType(named, named.Name, "System", "Runtime", "CompilerServices"));

    // A lambda that the code hands to System.Threading to run, such as the work of Task.Run, a continuation or a
    // thread. It runs elsewhere, without the function's thread, so it may block.
    private static bool RunsOffThread(IOperation operation)
    {
        if (operation is not IAnonymousFunctionOperation)
        {
            return false;
        }

        var parent = operation.Parent;
        while (parent is IDelegateCreationOperation or IConversionOperation)
        {
            parent = parent.Parent;
        }

        var method = parent?.Parent switch
        {
            IInvocationOperation invocation when parent is IArgumentOperation => invocation.TargetMethod,
            IObjectCreationOperation creation when parent is IArgumentOperation => creation.Constructor,
            _ => null,
        };

        var space = (method?.ReducedFrom ?? method)?.ContainingNamespace?.ToDisplayString();
        return space is "System.Threading" or "System.Threading.Tasks";
    }

    // The call that started a task, for the message: the method, or the delegate that was invoked.
    private static string Describe(IInvocationOperation call) =>
        call.TargetMethod.MethodKind == MethodKind.DelegateInvoke && call.Instance is { } instance
            ? instance switch
            {
                ILocalReferenceOperation local => local.Local.Name,
                IParameterReferenceOperation parameter => parameter.Parameter.Name,
                IFieldReferenceOperation field => field.Field.Name,
                IPropertyReferenceOperation property => property.Property.Name,
                _ => "a delegate",
            } + "()"
            : call.TargetMethod.Name + "()";

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
