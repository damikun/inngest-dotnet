// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;

namespace Inngest.SourceGeneration.Json;

// Writes [JsonSerializable(typeof(...))] on a JsonSerializerContext.
internal static class JsonSerializableEdit
{
    private const string AttributeName = "global::System.Text.Json.Serialization.JsonSerializable";

    private static readonly SyntaxAnnotation NeedsImports = new("Inngest.SourceGeneration.NeedsImports");

    // Adds one attribute for each type, in the order given, to the declaration the reference points to.
    public static async Task<Solution> AddAttributesAsync(Solution solution, SyntaxReference reference, IReadOnlyList<string> typeNames, CancellationToken cancellationToken)
    {
        var document = solution.GetDocument(reference.SyntaxTree);
        if (document is null)
        {
            return solution;
        }

        var root = await reference.SyntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
        if (root.FindNode(reference.Span) is not ClassDeclarationSyntax declaration)
        {
            return solution;
        }

        var lineEnding = LineEndingOf(root);
        var edited = declaration;
        foreach (var typeName in typeNames)
        {
            edited = WithAttribute(edited, typeName, lineEnding);
        }

        var changed = document.WithSyntaxRoot(root.ReplaceNode(declaration, edited));

        // The names above are written in full. This adds the using directives they need, then shortens them to the names that
        // the file can use.
        changed = await ImportAdder.AddImportsAsync(changed, NeedsImports, cancellationToken: cancellationToken).ConfigureAwait(false);
        changed = await Simplifier.ReduceAsync(changed, cancellationToken: cancellationToken).ConfigureAwait(false);
        changed = await Formatter.FormatAsync(changed, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        return changed.Project.Solution;
    }

    // The file's own line ending, so that the fix does not mix the two in a file.
    private static SyntaxTrivia LineEndingOf(SyntaxNode root)
    {
        foreach (var trivia in root.DescendantTrivia())
        {
            if (trivia.IsKind(SyntaxKind.EndOfLineTrivia))
            {
                return trivia;
            }
        }

        return SyntaxFactory.ElasticLineFeed;
    }

    private static ClassDeclarationSyntax WithAttribute(ClassDeclarationSyntax declaration, string typeName, SyntaxTrivia lineEnding)
    {
        var attribute = SyntaxFactory.Attribute(
            SyntaxFactory.ParseName(AttributeName).WithAdditionalAnnotations(Simplifier.Annotation, NeedsImports),
            SyntaxFactory.AttributeArgumentList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.AttributeArgument(SyntaxFactory.TypeOfExpression(
                    SyntaxFactory.ParseTypeName(typeName).WithAdditionalAnnotations(Simplifier.Annotation, NeedsImports))))));
        var list = SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(attribute)).WithAdditionalAnnotations(Formatter.Annotation);

        var lists = declaration.AttributeLists;
        if (lists.Count == 0)
        {
            // The attribute becomes the first token of the declaration, so it takes over the comments above the class,
            // and the class keeps the indentation.
            var first = declaration.GetFirstToken();
            var indentation = first.LeadingTrivia.LastOrDefault(static trivia => trivia.IsKind(SyntaxKind.WhitespaceTrivia));
            list = list.WithLeadingTrivia(first.LeadingTrivia).WithTrailingTrivia(lineEnding);
            declaration = declaration.ReplaceToken(first, first.WithLeadingTrivia(indentation));
            return declaration.WithAttributeLists(declaration.AttributeLists.Insert(0, list));
        }

        // After the last [JsonSerializable], or at the end when the class has none yet, with the layout of the list before.
        var index = lists.Count;
        for (var i = lists.Count - 1; i >= 0; i--)
        {
            if (lists[i].Attributes.Any(static a => IsJsonSerializable(a.Name)))
            {
                index = i + 1;
                break;
            }
        }

        var neighbor = lists[index - 1];
        var neighborIndentation = neighbor.GetLeadingTrivia().LastOrDefault(static trivia => trivia.IsKind(SyntaxKind.WhitespaceTrivia));
        list = list.WithLeadingTrivia(neighborIndentation).WithTrailingTrivia(neighbor.GetTrailingTrivia());
        return declaration.WithAttributeLists(lists.Insert(index, list));
    }

    private static bool IsJsonSerializable(NameSyntax name)
    {
        var last = name is QualifiedNameSyntax qualified ? qualified.Right : name;
        return last is SimpleNameSyntax { Identifier.ValueText: "JsonSerializable" or "JsonSerializableAttribute" };
    }
}
