// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Inngest.SourceGeneration.Functions;

/// <summary>
/// In a project that generates AddInngestFunctions(), says why it leaves out a class with [InngestFunction], or why the
/// attribute does nothing on it.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class FunctionClassAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        DiagnosticDescriptors.NotAFunction,
        DiagnosticDescriptors.AbstractFunction,
        DiagnosticDescriptors.GenericFunction,
        DiagnosticDescriptors.InaccessibleFunction,
        DiagnosticDescriptors.NoPublicConstructor);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    // The diagnostics are about what the generated method does with a class, so a project that has not turned generation on
    // gets none: a generic class that it registers by hand, or a private one it registers from inside its container, is
    // not a mistake there.
    private static void OnCompilationStart(CompilationStartAnalysisContext start)
    {
        if (RegistrationOptions.IsEnabled(start.Options.AnalyzerConfigOptionsProvider.GlobalOptions))
        {
            start.RegisterSymbolAction(AnalyzeType, SymbolKind.NamedType);
        }
    }

    private static void AnalyzeType(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (!HasFunctionAttribute(type))
        {
            return;
        }

        var descriptor = FunctionClassification.Classify(type) switch
        {
            FunctionClassProblem.NotAFunction => DiagnosticDescriptors.NotAFunction,
            FunctionClassProblem.Abstract => DiagnosticDescriptors.AbstractFunction,
            FunctionClassProblem.Generic => DiagnosticDescriptors.GenericFunction,
            FunctionClassProblem.Inaccessible => DiagnosticDescriptors.InaccessibleFunction,
            FunctionClassProblem.NoPublicConstructor => DiagnosticDescriptors.NoPublicConstructor,
            _ => null,
        };

        if (descriptor is not null)
        {
            context.ReportDiagnostic(Diagnostic.Create(descriptor, type.Locations.FirstOrDefault() ?? Location.None, type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)));
        }
    }

    private static bool HasFunctionAttribute(INamedTypeSymbol type)
    {
        foreach (var attribute in type.GetAttributes())
        {
            if (InngestTypes.IsFunctionAttribute(attribute.AttributeClass))
            {
                return true;
            }
        }

        return false;
    }
}
