// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Microsoft.CodeAnalysis;

namespace Inngest.SourceGeneration.Functions;

// Decides whether the generated AddInngestFunctions() can register a class. The generator uses it to leave a class out
// and the analyzer to say why, so the two never disagree.
internal static class FunctionClassification
{
    public static FunctionClassProblem Classify(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Class)
        {
            return FunctionClassProblem.NotAFunction;
        }

        switch (DerivesFromFunction(type))
        {
            case Derivation.Unresolved:
                return FunctionClassProblem.Unresolved;
            case Derivation.No:
                return FunctionClassProblem.NotAFunction;
        }

        if (type.IsAbstract)
        {
            return FunctionClassProblem.Abstract;
        }

        // AddFunction<T>() needs a constructed type, and a generic class does not say what its type arguments are.
        for (var container = type; container is not null; container = container.ContainingType)
        {
            if (container.Arity > 0)
            {
                return FunctionClassProblem.Generic;
            }
        }

        // The registration is a static method outside the class, so every type from the class out to its namespace must
        // be reachable from there.
        for (var container = type; container is not null; container = container.ContainingType)
        {
            if (container.IsFileLocal || container.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
            {
                return FunctionClassProblem.Inaccessible;
            }
        }

        // The service provider constructs the class from its public constructors, so without one every call fails.
        foreach (var constructor in type.InstanceConstructors)
        {
            if (constructor.DeclaredAccessibility == Accessibility.Public)
            {
                return FunctionClassProblem.None;
            }
        }

        return FunctionClassProblem.NoPublicConstructor;
    }

    // The InngestFunction<TEventData, TResult> a function class derives from, whichever of the two generic bases it names.
    public static INamedTypeSymbol? FindFunctionBase(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (InngestTypes.IsFunctionWithResult(current))
            {
                return current;
            }
        }

        return null;
    }

    private static Derivation DerivesFromFunction(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current is IErrorTypeSymbol)
            {
                return Derivation.Unresolved;
            }

            if (InngestTypes.IsFunctionRoot(current))
            {
                return Derivation.Yes;
            }
        }

        return Derivation.No;
    }

    private enum Derivation
    {
        Yes,
        No,
        Unresolved,
    }
}
