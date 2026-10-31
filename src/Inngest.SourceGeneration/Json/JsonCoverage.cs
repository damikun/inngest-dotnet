// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace Inngest.SourceGeneration.Json;

// The types that source-generated System.Text.Json metadata exists for in a compilation: the types a JsonSerializerContext
// lists, the types Inngest's own context lists, and every type those reach, because the generator writes metadata for the
// whole graph of a type it is asked for. A type outside fails with a NotSupportedException when reflection is off, however
// ordinary it is: an int is only there if something that is listed has an int.
//
// "Reach" is what the generator of System.Text.Json does, which was checked against it: the types of the public properties
// and fields (of non-public ones only with [JsonInclude]), without those marked [JsonIgnore]; the parameters of the one
// constructor it would call; the element type of a collection, whose own members and constructors it ignores; the type
// arguments of framework generics; [JsonDerivedType]. A type with a [JsonConverter] of its own has nothing reached through it.
internal sealed class JsonCoverage
{
    // The types Inngest's PublicTypesJsonContext lists, which the SDK adds to the serializer options of every app. A test
    // compares them with the context, so the two cannot drift apart.
    internal static readonly string[] SdkTypes =
    [
        "Inngest.FunctionFailure",
        "Inngest.FunctionError",
        "Inngest.FunctionFinished",
        "Inngest.FunctionCancelled",
        "Inngest.SendEventsResult",
    ];

    private readonly HashSet<ITypeSymbol> _covered = new(SymbolEqualityComparer.Default);

    internal IReadOnlyCollection<ITypeSymbol> Types => _covered;

    public static JsonCoverage Create(Compilation compilation)
    {
        var coverage = new JsonCoverage();
        coverage.AddSdkContext(compilation);

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

    private void AddSdkContext(Compilation compilation)
    {
        Add(compilation.GetSpecialType(SpecialType.System_Object));
        if (compilation.GetTypeByMetadataName("System.Text.Json.JsonElement") is { } element)
        {
            Add(element);
        }

        foreach (var name in SdkTypes)
        {
            if (compilation.GetTypeByMetadataName(name) is { } type && InngestTypes.IsDeclaredBySdk(type))
            {
                Add(type);
            }
        }
    }

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
            }
            else if (type is INamedTypeSymbol named)
            {
                PushReached(named, pending);
            }
        }
    }

    private static void PushReached(INamedTypeSymbol type, Stack<ITypeSymbol> pending)
    {
        // The metadata of a collection, a nullable or a tuple comes with the types it holds, and a framework type has no
        // members of the app's to reach.
        if (IsFrameworkType(type))
        {
            foreach (var argument in type.TypeArguments)
            {
                pending.Push(argument);
            }

            return;
        }

        // A converter reads and writes the type itself, and the generator looks no further.
        if (type.TypeKind == TypeKind.Enum || HasAttribute(type, "JsonConverterAttribute"))
        {
            return;
        }

        foreach (var data in type.GetAttributes())
        {
            if (IsJsonAttribute(data.AttributeClass, "JsonDerivedTypeAttribute") && data.ConstructorArguments.Length > 0 && data.ConstructorArguments[0].Value is ITypeSymbol derived)
            {
                pending.Push(derived);
            }
        }

        // A collection is written as its elements, and its own members and constructors are not part of it, whether the
        // framework or the app declares it: class Items : List<Line> reaches Line and nothing else.
        if (ElementTypeOf(type) is { } element)
        {
            pending.Push(element);
            return;
        }

        for (var current = type; current is not null && !IsFrameworkType(current); current = current.BaseType)
        {
            PushMembers(current, pending);
        }

        if (type.TypeKind == TypeKind.Interface)
        {
            foreach (var inherited in type.AllInterfaces)
            {
                PushMembers(inherited, pending);
            }
        }

        if (SelectConstructor(type) is { } constructor)
        {
            foreach (var parameter in constructor.Parameters)
            {
                pending.Push(parameter.Type);
            }
        }
    }

    private static void PushMembers(INamedTypeSymbol type, Stack<ITypeSymbol> pending)
    {
        foreach (var member in type.GetMembers())
        {
            switch (member)
            {
                case IPropertySymbol { IsStatic: false, IsIndexer: false } property when IsSerialized(property, property.DeclaredAccessibility):
                    pending.Push(property.Type);
                    break;
                case IFieldSymbol { IsStatic: false, IsImplicitlyDeclared: false } field when IsSerialized(field, field.DeclaredAccessibility):
                    pending.Push(field.Type);
                    break;
            }
        }
    }

    // Public members are written, and so are non-public ones that [JsonInclude] asks for and the generated code can reach (a
    // private one fails when the context is used). [JsonIgnore] leaves a member out unless it only names a condition.
    private static bool IsSerialized(ISymbol member, Accessibility accessibility) =>
        !IsIgnored(member) && (accessibility == Accessibility.Public || (accessibility != Accessibility.Private && HasAttribute(member, "JsonIncludeAttribute")));

    private static bool IsIgnored(ISymbol member)
    {
        foreach (var data in member.GetAttributes())
        {
            if (!IsJsonAttribute(data.AttributeClass, "JsonIgnoreAttribute"))
            {
                continue;
            }

            // The condition is Always when none is named.
            foreach (var argument in data.NamedArguments)
            {
                if (argument.Key == "Condition")
                {
                    return argument.Value.Value is int condition && condition == AlwaysIgnore;
                }
            }

            return true;
        }

        return false;
    }

    // JsonIgnoreCondition.Always.
    private const int AlwaysIgnore = 1;

    // The constructor the generator calls to read the type, whose parameters it needs metadata for: the one marked
    // [JsonConstructor], else the only public one when the class has no public constructor without parameters. A struct and
    // a class with a default constructor are created without a call, and a class with several public constructors and no
    // mark has none chosen.
    private static IMethodSymbol? SelectConstructor(INamedTypeSymbol type)
    {
        if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct))
        {
            return null;
        }

        IMethodSymbol? onlyPublic = null;
        var publicCount = 0;
        var hasDefault = false;
        foreach (var constructor in type.InstanceConstructors)
        {
            if (HasAttribute(constructor, "JsonConstructorAttribute"))
            {
                return constructor;
            }

            if (constructor.DeclaredAccessibility == Accessibility.Public)
            {
                publicCount++;
                onlyPublic = constructor;
                hasDefault |= constructor.Parameters.Length == 0;
            }
        }

        return type.TypeKind == TypeKind.Class && !hasDefault && publicCount == 1 ? onlyPublic : null;
    }

    // T of the IEnumerable<T> a type implements: what the generator writes it as.
    private static ITypeSymbol? ElementTypeOf(INamedTypeSymbol type)
    {
        foreach (var contract in type.AllInterfaces)
        {
            if (contract is { Name: "IEnumerable", Arity: 1, ContainingNamespace: { Name: "Generic", ContainingNamespace: { Name: "Collections", ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } } } })
            {
                return contract.TypeArguments[0];
            }
        }

        return null;
    }

    private static bool HasAttribute(ISymbol symbol, string name)
    {
        foreach (var data in symbol.GetAttributes())
        {
            if (IsJsonAttribute(data.AttributeClass, name))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsJsonAttribute(INamedTypeSymbol? attribute, string name) =>
        attribute is { ContainingNamespace: { Name: "Serialization", ContainingNamespace: { Name: "Json", ContainingNamespace: { Name: "Text", ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } } } } }
        && attribute.Name == name;

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
