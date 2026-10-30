// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System;
using System.Collections.Immutable;
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

        // RunAsync<T> saves what the handler returns with the run, and reads it back when the run continues.
        if (InngestTypes.IsStepTools(method.ContainingType) && method.Name == "RunAsync" && method.TypeArguments.Length == 1)
        {
            Check(method.TypeArguments[0], NameLocation(invocation), "the result of a step", coverage, context.ReportDiagnostic);
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

    // What the SDK serializes with a context of its own, and what is not a concrete type yet, needs nothing from the app.
    private static bool NeedsListing(ITypeSymbol type) =>
        type is not (IErrorTypeSymbol or IDynamicTypeSymbol or IPointerTypeSymbol)
        && type.SpecialType is not (SpecialType.System_Object or SpecialType.System_Void)
        && !ContainsTypeParameter(type)
        && !InngestTypes.IsDeclaredBySdk(type)
        && !IsJsonElement(type);

    private static bool IsJsonElement(ITypeSymbol type) =>
        type is { Name: "JsonElement", ContainingNamespace: { Name: "Json", ContainingNamespace: { Name: "Text", ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } } } };

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

        return type.Locations[0];
    }

    // The method name of the call, so that the squiggle does not cover the lambda that follows it.
    private static Location NameLocation(IInvocationOperation invocation) =>
        invocation.Syntax is InvocationExpressionSyntax { Expression: var expression }
            ? (expression is MemberAccessExpressionSyntax member ? member.Name : expression).GetLocation()
            : invocation.Syntax.GetLocation();
}
