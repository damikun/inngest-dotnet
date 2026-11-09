// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Inngest.SourceGeneration.Steps;

// Finds a value in a step id that is different in every call: the clock, a new Guid, a random number. It looks at the
// expression and, one level deep, at the initializer of a local the expression reads, which is how an id is usually
// built. Lambdas are skipped, since a value made inside a step is memoized with it, and so are the parts of a
// StepOptions other than the id, such as its display name.
internal static class Nondeterminism
{
    public static string? Find(IOperation operation) => Find(operation, followLocals: true);

    private static string? Find(IOperation operation, bool followLocals)
    {
        switch (operation)
        {
            case IAnonymousFunctionOperation or IDelegateCreationOperation:
                return null;
            case IObjectCreationOperation creation when InngestTypes.IsStepOptions(creation.Type):
                return FindInStepOptions(creation, followLocals);
            case IPropertyReferenceOperation property when Describe(property.Property) is { } clock:
                return clock;
            case IInvocationOperation invocation when Describe(invocation) is { } call:
                return call;
            case ILocalReferenceOperation local when followLocals:
                return FindInInitializer(local, operation.SemanticModel);
        }

        foreach (var child in operation.ChildOperations)
        {
            if (Find(child, followLocals) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static string? FindInStepOptions(IObjectCreationOperation creation, bool followLocals)
    {
        foreach (var argument in creation.Arguments)
        {
            if (Find(argument.Value, followLocals) is { } found)
            {
                return found;
            }
        }

        if (creation.Initializer is not { } initializer)
        {
            return null;
        }

        foreach (var member in initializer.Initializers)
        {
            if (member is ISimpleAssignmentOperation { Target: IPropertyReferenceOperation { Property.Name: "Id" } } assignment
                && Find(assignment.Value, followLocals) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static string? FindInInitializer(ILocalReferenceOperation reference, SemanticModel? model)
    {
        if (model is null)
        {
            return null;
        }

        foreach (var declaration in reference.Local.DeclaringSyntaxReferences)
        {
            if (declaration.SyntaxTree == model.SyntaxTree
                && declaration.GetSyntax() is VariableDeclaratorSyntax { Initializer.Value: { } value }
                && model.GetOperation(value) is { } initializer
                && Find(initializer, followLocals: false) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static string? Describe(IPropertySymbol property)
    {
        var type = property.ContainingType;
        if (IsSystemType(type, "DateTime") || IsSystemType(type, "DateTimeOffset"))
        {
            return property.Name is "Now" or "UtcNow" or "Today" ? $"{type.Name}.{property.Name}" : null;
        }

        return IsSystemType(type, "Environment") && property.Name is "TickCount" or "TickCount64" ? $"Environment.{property.Name}" : null;
    }

    private static string? Describe(IInvocationOperation invocation)
    {
        var method = invocation.TargetMethod;
        var type = method.ContainingType;
        if (IsSystemType(type, "Guid"))
        {
            return method.Name is "NewGuid" or "CreateVersion7" ? $"Guid.{method.Name}()" : null;
        }

        if (IsSystemType(type, "Random"))
        {
            // new Random(seed) gives the same numbers in every call.
            return invocation.Instance is IObjectCreationOperation { Arguments.Length: > 0 } ? null : $"Random.{method.Name}()";
        }

        if (IsType(type, "RandomNumberGenerator", "System", "Security", "Cryptography") && method.IsStatic)
        {
            return $"RandomNumberGenerator.{method.Name}()";
        }

        if (IsSystemType(type, "TimeProvider"))
        {
            return method.Name is "GetUtcNow" or "GetLocalNow" or "GetTimestamp" ? $"TimeProvider.{method.Name}()" : null;
        }

        return IsType(type, "Stopwatch", "System", "Diagnostics") && method.Name == "GetTimestamp" ? "Stopwatch.GetTimestamp()" : null;
    }

    private static bool IsSystemType(INamedTypeSymbol? type, string name) => IsType(type, name, "System");

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
