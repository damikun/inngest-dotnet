// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace Inngest.SourceGeneration.Json;

// The types that source-generated System.Text.Json metadata exists for in a compilation: the types a JsonSerializerContext
// lists, and every type those reach through their members, because the generator writes metadata for the whole graph
// of a type it is asked for. A type outside both fails with a NotSupportedException when reflection is off, however
// ordinary it is: an int is only there if something that is listed has an int.
internal sealed class JsonCoverage
{
    private readonly HashSet<ITypeSymbol> _covered = new(SymbolEqualityComparer.Default);

    public static JsonCoverage Create(Compilation compilation)
    {
        var coverage = new JsonCoverage();
        var context = compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonSerializerContext");
        var attribute = compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonSerializableAttribute");
        if (context is null || attribute is null)
        {
            return coverage;
        }

        coverage.Collect(compilation.Assembly.GlobalNamespace, context, attribute);
        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            if (MayDefineContexts(assembly))
            {
                coverage.Collect(assembly.GlobalNamespace, context, attribute);
            }
        }

        return coverage;
    }

    public bool Covers(ITypeSymbol type) => _covered.Contains(type);

    // A context in another project of the app counts. The framework's own assemblies do not define any a project could
    // use, and there are hundreds of them, so only the others that can use System.Text.Json are searched.
    private static bool MayDefineContexts(IAssemblySymbol assembly)
    {
        var name = assembly.Name;
        if (name == "Inngest" || name == "netstandard" || name == "mscorlib" || name.StartsWith("System.", StringComparison.Ordinal) || name.StartsWith("Microsoft.", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var module in assembly.Modules)
        {
            foreach (var reference in module.ReferencedAssemblies)
            {
                if (reference.Name == "System.Text.Json")
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void Collect(INamespaceSymbol container, INamedTypeSymbol contextBase, INamedTypeSymbol attribute)
    {
        foreach (var type in container.GetTypeMembers())
        {
            CollectType(type, contextBase, attribute);
        }

        foreach (var child in container.GetNamespaceMembers())
        {
            Collect(child, contextBase, attribute);
        }
    }

    private void CollectType(INamedTypeSymbol type, INamedTypeSymbol contextBase, INamedTypeSymbol attribute)
    {
        if (DerivesFrom(type, contextBase))
        {
            foreach (var data in type.GetAttributes())
            {
                if (SymbolEqualityComparer.Default.Equals(data.AttributeClass, attribute)
                    && data.ConstructorArguments.Length == 1
                    && data.ConstructorArguments[0].Value is ITypeSymbol listed)
                {
                    Add(listed);
                }
            }
        }

        foreach (var nested in type.GetTypeMembers())
        {
            CollectType(nested, contextBase, attribute);
        }
    }

    private static bool DerivesFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
            {
                return true;
            }
        }

        return false;
    }

    // A work list rather than recursion: the graph of a domain model can be deep, and the analyzer runs on a thread whose
    // stack the compiler sized.
    private void Add(ITypeSymbol listed)
    {
        var pending = new Stack<ITypeSymbol>();
        pending.Push(listed);
        while (pending.Count > 0)
        {
            var type = pending.Pop();
            if (type is IErrorTypeSymbol or ITypeParameterSymbol or IPointerTypeSymbol or IFunctionPointerTypeSymbol or IDynamicTypeSymbol || !_covered.Add(type))
            {
                continue;
            }

            if (type is IArrayTypeSymbol array)
            {
                pending.Push(array.ElementType);
                continue;
            }

            if (type is not INamedTypeSymbol named)
            {
                continue;
            }

            foreach (var argument in named.TypeArguments)
            {
                pending.Push(argument);
            }

            // The metadata of a collection or a primitive comes with the types it holds, and a framework type has no
            // members of the app's to reach.
            for (var current = named; current is not null && !IsFrameworkType(current); current = current.BaseType)
            {
                PushMembers(current, pending);
            }
        }
    }

    private static void PushMembers(INamedTypeSymbol type, Stack<ITypeSymbol> pending)
    {
        foreach (var member in type.GetMembers())
        {
            switch (member)
            {
                case IPropertySymbol { IsStatic: false, IsIndexer: false } property:
                    pending.Push(property.Type);
                    break;
                case IFieldSymbol { IsStatic: false, IsImplicitlyDeclared: false } field:
                    pending.Push(field.Type);
                    break;
                case IMethodSymbol { MethodKind: MethodKind.Constructor, DeclaredAccessibility: Accessibility.Public } constructor:
                    foreach (var parameter in constructor.Parameters)
                    {
                        pending.Push(parameter.Type);
                    }

                    break;
            }
        }

        // [JsonDerivedType(typeof(Circle))] on a base type adds the derived type to the metadata.
        foreach (var data in type.GetAttributes())
        {
            if (data.AttributeClass?.Name == "JsonDerivedTypeAttribute" && data.ConstructorArguments.Length > 0 && data.ConstructorArguments[0].Value is ITypeSymbol derived)
            {
                pending.Push(derived);
            }
        }
    }

    private static bool IsFrameworkType(INamedTypeSymbol type)
    {
        var container = type.ContainingNamespace;
        while (container is { IsGlobalNamespace: false })
        {
            if (container.ContainingNamespace is { IsGlobalNamespace: true })
            {
                return container.Name is "System" or "Microsoft";
            }

            container = container.ContainingNamespace;
        }

        return false;
    }
}
