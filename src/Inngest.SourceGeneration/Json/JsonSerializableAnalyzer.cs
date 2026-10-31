// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System;
using System.Collections.Immutable;
using System.Linq;
using Inngest.SourceGeneration.Functions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Inngest.SourceGeneration.Json;

/// <summary>
/// In an app that turns reflection-based System.Text.Json off, reports the types that Inngest serializes for a function
/// and that no JsonSerializerContext lists with [JsonSerializable].
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class JsonSerializableAnalyzer : DiagnosticAnalyzer
{
    // The property of the diagnostic that carries the type for the code fix, as code refers to it.
    internal const string TypeProperty = "Type";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        DiagnosticDescriptors.TypeNotInJsonContext,
        DiagnosticDescriptors.AnonymousType);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext start)
    {
        // Nothing is registered unless the app turns reflection off, so an ordinary app pays for none of this.
        if (!ReflectionJson.IsDisabled(start.Options.AnalyzerConfigOptionsProvider.GlobalOptions))
        {
            return;
        }

        // Found when the first type needs checking: most files hold no function, and the search reads every type of the
        // project and of the assemblies it uses.
        var coverage = new Lazy<JsonCoverage>(() => JsonCoverage.Create(start.Compilation));
        start.RegisterSymbolAction(context => AnalyzeFunctionClass(context, coverage), SymbolKind.NamedType);
        start.RegisterOperationAction(context => AnalyzeInvocation(context, coverage), OperationKind.Invocation);
    }

    private static void AnalyzeFunctionClass(SymbolAnalysisContext context, Lazy<JsonCoverage> coverage)
    {
        var type = (INamedTypeSymbol)context.Symbol;

        // An abstract class is a base the functions derive from, and the functions are checked with their own type arguments.
        if (type.TypeKind != TypeKind.Class || type.IsAbstract || FunctionClassification.FindFunctionBase(type) is not { } functionBase)
        {
            return;
        }

        var name = type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        Check(functionBase.TypeArguments[0], LocationOfBaseTypeArgument(type, 0), $"the event data of function '{name}'", coverage, context.ReportDiagnostic);
        Check(functionBase.TypeArguments[1], LocationOfBaseTypeArgument(type, 1), $"the result of function '{name}'", coverage, context.ReportDiagnostic);
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context, Lazy<JsonCoverage> coverage)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;

        if (method.TypeArguments.Length == 0)
        {
            return;
        }

        if (InngestTypes.IsBuilderExtensions(method.ContainingType))
        {
            // AddFunction<TEventData>(options, handler) registers a delegate. The class overloads constrain their type
            // parameter to a function class, and their classes are checked where they are declared.
            if (method.Name == "AddFunction" && method.TypeArguments.Length == 1 && method.TypeParameters[0].ConstraintTypes.IsEmpty)
            {
                Check(method.TypeArguments[0], NameLocation(invocation), "the event data of a function registered with a delegate", coverage, context.ReportDiagnostic);
            }

            return;
        }

        if (InngestTypes.IsStepTools(method.ContainingType))
        {
            CheckStep(method, NameLocation(invocation), coverage, context.ReportDiagnostic);
        }
    }

    // A step saves what it returns with the run, and reads it back when the run continues, through the serializer options
    // of the app, so the type argument that names it must be in the context: RunAsync<T> and AIWrapAsync, the data
    // WaitForEventAsync and WaitForSignalAsync wait for, the result of InvokeAsync, the response of InferAsync and the data
    // PublishAsync publishes. The data that InvokeAsync and SendEventAsync send are values of whatever type the code gives
    // them, and are left to the app.
    private static void CheckStep(IMethodSymbol method, Location location, Lazy<JsonCoverage> coverage, Action<Diagnostic> report)
    {
        var arguments = method.TypeArguments;
        switch (method.Name)
        {
            case "RunAsync" when arguments.Length == 1:
                Check(arguments[0], location, "the result of a step", coverage, report);
                break;
            case "AIWrapAsync" when arguments.Length == 2:
                Check(arguments[0], location, "the input of a step", coverage, report);
                Check(arguments[1], location, "the result of a step", coverage, report);
                break;
            case "WaitForEventAsync" when arguments.Length == 1:
                Check(arguments[0], location, "the data of an event that a step waits for", coverage, report);
                break;
            case "WaitForSignalAsync" when arguments.Length == 1:
                Check(arguments[0], location, "the data of a signal that a step waits for", coverage, report);
                break;
            case "InvokeAsync" when arguments.Length is 1 or 2:
                // InvokeAsync<TResult> and InvokeAsync<TFunction, TResult>.
                Check(arguments[arguments.Length - 1], location, "the result of an invoked function", coverage, report);
                break;
            case "InferAsync" when arguments.Length == 1:
                Check(arguments[0], location, "the response of an inference step", coverage, report);
                break;
            case "PublishAsync" when arguments.Length == 1:
                Check(arguments[0], location, "the data a step publishes", coverage, report);
                break;
        }
    }

    private static void Check(ITypeSymbol type, Location location, string usage, Lazy<JsonCoverage> coverage, Action<Diagnostic> report)
    {
        if (!NeedsListing(type))
        {
            return;
        }

        if (ContainsAnonymousType(type))
        {
            report(Diagnostic.Create(DiagnosticDescriptors.AnonymousType, location, char.ToUpperInvariant(usage[0]) + usage.Substring(1)));
            return;
        }

        if (coverage.Value.Covers(type))
        {
            return;
        }

        var properties = ImmutableDictionary<string, string?>.Empty.Add(TypeProperty, type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        report(Diagnostic.Create(
            DiagnosticDescriptors.TypeNotInJsonContext,
            location,
            properties,
            type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            usage));
    }

    // What is left to check is any concrete type: the ones the SDK's own context lists are in the coverage already.
    private static bool NeedsListing(ITypeSymbol type) =>
        type is not (IErrorTypeSymbol or IDynamicTypeSymbol or IPointerTypeSymbol)
        && type.SpecialType != SpecialType.System_Void
        && !ContainsTypeParameter(type);

    private static bool ContainsTypeParameter(ITypeSymbol type) =>
        type switch
        {
            ITypeParameterSymbol => true,
            IArrayTypeSymbol array => ContainsTypeParameter(array.ElementType),
            INamedTypeSymbol named => AnyTypeArgument(named, ContainsTypeParameter),
            _ => false,
        };

    private static bool ContainsAnonymousType(ITypeSymbol type) =>
        type switch
        {
            IArrayTypeSymbol array => ContainsAnonymousType(array.ElementType),
            INamedTypeSymbol named => named.IsAnonymousType || AnyTypeArgument(named, ContainsAnonymousType),
            _ => false,
        };

    private static bool AnyTypeArgument(INamedTypeSymbol type, Func<ITypeSymbol, bool> predicate)
    {
        foreach (var argument in type.TypeArguments)
        {
            if (predicate(argument))
            {
                return true;
            }
        }

        return false;
    }

    // The type argument in `class Order : InngestFunction<OrderPlaced, Receipt>`, so the squiggle is under the type that is
    // missing. A class that reaches InngestFunction through a base class of its own is reported on its name.
    private static Location LocationOfBaseTypeArgument(INamedTypeSymbol type, int index)
    {
        foreach (var reference in type.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is not ClassDeclarationSyntax { BaseList: { } baseList })
            {
                continue;
            }

            foreach (var baseType in baseList.Types)
            {
                var name = baseType.Type is QualifiedNameSyntax qualified ? qualified.Right : baseType.Type;
                if (name is GenericNameSyntax { Identifier.ValueText: "InngestFunction" } generic && index < generic.TypeArgumentList.Arguments.Count)
                {
                    return generic.TypeArgumentList.Arguments[index].GetLocation();
                }
            }
        }

        return type.Locations.FirstOrDefault() ?? Location.None;
    }

    // The method name of the call, so that the squiggle does not cover the lambda that follows it.
    private static Location NameLocation(IInvocationOperation invocation)
    {
        if (invocation.Syntax is not InvocationExpressionSyntax { Expression: var expression })
        {
            return invocation.Syntax.GetLocation();
        }

        var name = expression is MemberAccessExpressionSyntax member ? member.Name : expression;
        return (name is SimpleNameSyntax simple ? simple.Identifier.GetLocation() : name.GetLocation());
    }
}
