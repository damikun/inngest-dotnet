// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Inngest.SourceGeneration.Functions;

/// <summary>
/// Writes AddInngestFunctions() for the assembly it compiles: an extension method on IInngestBuilder that calls
/// AddFunction&lt;T&gt;() for each class with [InngestFunction], so the app lists none of them and no code scans for them.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class FunctionRegistrationGenerator : IIncrementalGenerator
{
    // Names for the steps of the pipeline, which tests look up to see what ran again after an edit.
    internal static class StepNames
    {
        public const string FunctionClasses = nameof(FunctionClasses);
        public const string AssemblyName = nameof(AssemblyName);
        public const string Options = nameof(Options);
        public const string Registration = nameof(Registration);
    }

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // The compiler finds the classes with the attribute from an index of the attribute names in each file, instead of
        // handing every syntax node to the generator, which is what makes this cheap enough to run on every keystroke.
        var functionClasses = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                InngestTypes.FunctionAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (attribute, _) => Describe(attribute.TargetSymbol))
            .Where(static typeName => typeName is not null)
            .Select(static (typeName, _) => typeName!)
            .WithTrackingName(StepNames.FunctionClasses);

        var assemblyName = context.CompilationProvider
            .Select(static (compilation, _) => compilation.AssemblyName ?? "Assembly")
            .WithTrackingName(StepNames.AssemblyName);

        var options = context.AnalyzerConfigOptionsProvider
            .Select(static (provider, _) => RegistrationOptions.Read(provider.GlobalOptions))
            .WithTrackingName(StepNames.Options);

        var registration = functionClasses.Collect()
            .Combine(assemblyName)
            .Combine(options)
            .Select(static (input, _) => Registration.Create(input.Left.Left, input.Left.Right, input.Right))
            .WithTrackingName(StepNames.Registration);

        context.RegisterSourceOutput(registration, static (output, value) => Emit(output, value));
    }

    // The name of the class as code refers to it, or null when the class cannot be registered. The analyzer says why.
    private static string? Describe(ISymbol symbol) =>
        symbol is INamedTypeSymbol type && FunctionClassification.Classify(type) == FunctionClassProblem.None
            ? type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            : null;

    private static void Emit(SourceProductionContext context, Registration registration)
    {
        if (registration.TypeNames.Length == 0)
        {
            return;
        }

        var options = registration.Options;
        if (options.RejectedMethodName is { } methodName)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.InvalidOption,
                Location.None,
                RegistrationOptions.MethodNameProperty,
                methodName,
                "a C# identifier",
                RegistrationOptions.DefaultMethodName));
        }

        if (options.RejectedAccessibility is { } accessibility)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.InvalidOption,
                Location.None,
                RegistrationOptions.AccessibilityProperty,
                accessibility,
                "public or internal",
                "internal"));
        }

        var version = typeof(FunctionRegistrationGenerator).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
        context.AddSource(
            FunctionRegistrationEmitter.HintName,
            SourceText.From(FunctionRegistrationEmitter.Emit(registration, version), Encoding.UTF8));
    }
}
