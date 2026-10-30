// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Microsoft.CodeAnalysis;

namespace Inngest.SourceGeneration;

// Finds the SDK's types in a compilation by their names. A name lookup (GetTypeByMetadataName) returns nothing when two
// references define the same type, and then the generator and the analyzers would go quiet without saying so; comparing
// the namespace and the assembly of the symbol itself never has that gap.
internal static class InngestTypes
{
    public const string FunctionAttributeMetadataName = "Inngest.InngestFunctionAttribute";

    public static bool IsFunctionAttribute(INamedTypeSymbol? type) => IsInngestType(type, "InngestFunctionAttribute");

    // The base of every function class. The generic ones derive from it, so a class that reaches it is a function.
    public static bool IsFunctionRoot(INamedTypeSymbol type) => IsInngestType(type, "InngestFunction") && !type.IsGenericType;

    // InngestFunction<TEventData, TResult>, which InngestFunction<TEventData> derives from with an object result.
    public static bool IsFunctionWithResult(INamedTypeSymbol type) =>
        IsInngestType(type.OriginalDefinition, "InngestFunction") && type.Arity == 2;

    public static bool IsStepTools(INamedTypeSymbol? type) => IsInngestType(type, "IStepTools") || IsInngestType(type, "StepToolsExtensions");

    public static bool IsBuilderExtensions(INamedTypeSymbol? type) => IsInngestType(type, "InngestBuilderExtensions");

    // Anything the SDK declares is serialized by the SDK itself: its own context covers FunctionFailure, SendEventsResult
    // and the like, so the app never lists them.
    public static bool IsDeclaredBySdk(ITypeSymbol type) => type.ContainingAssembly?.Name == "Inngest";

    private static bool IsInngestType(INamedTypeSymbol? type, string name) =>
        type is not null
        && type.Name == name
        && type.ContainingNamespace is { Name: "Inngest", ContainingNamespace.IsGlobalNamespace: true }
        && type.ContainingAssembly?.Name == "Inngest";
}
