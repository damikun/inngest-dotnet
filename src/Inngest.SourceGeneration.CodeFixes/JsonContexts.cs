// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Inngest.SourceGeneration.Json;

// The JsonSerializerContext classes of the project that the code fix can edit.
internal static class JsonContexts
{
    // The declaration to edit is the first one in a file of the project's own: a context is partial, and the generator
    // writes the other part, which the fix must not touch.
    public static IEnumerable<(INamedTypeSymbol Symbol, SyntaxReference Reference)> FindDeclarations(Compilation compilation, Solution solution)
    {
        var contextBase = compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonSerializerContext");
        if (contextBase is null)
        {
            yield break;
        }

        var pending = new Stack<INamespaceOrTypeSymbol>();
        pending.Push(compilation.Assembly.GlobalNamespace);
        while (pending.Count > 0)
        {
            foreach (var member in pending.Pop().GetMembers())
            {
                if (member is INamespaceOrTypeSymbol container)
                {
                    pending.Push(container);
                }

                if (member is INamedTypeSymbol type && DerivesFrom(type, contextBase) && FirstEditableDeclaration(type, solution) is { } reference)
                {
                    yield return (type, reference);
                }
            }
        }
    }

    private static SyntaxReference? FirstEditableDeclaration(INamedTypeSymbol type, Solution solution)
    {
        foreach (var reference in type.DeclaringSyntaxReferences)
        {
            if (solution.GetDocument(reference.SyntaxTree) is { } document and not SourceGeneratedDocument
                && reference.GetSyntax() is ClassDeclarationSyntax)
            {
                return reference;
            }
        }

        return null;
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
}
