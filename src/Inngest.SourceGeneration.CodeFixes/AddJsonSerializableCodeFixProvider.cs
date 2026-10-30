// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;

namespace Inngest.SourceGeneration.Json;

/// <summary>
/// Lists the missing type on a JsonSerializerContext of the project with [JsonSerializable(typeof(...))], one fix for each
/// context there is.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AddJsonSerializableCodeFixProvider))]
[Shared]
public sealed class AddJsonSerializableCodeFixProvider : CodeFixProvider
{
    private const string AttributeName = "global::System.Text.Json.Serialization.JsonSerializable";

    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create(DiagnosticIds.TypeNotInJsonContext);

    // Several diagnostics ask for the same attribute on the same class, and merging their edits one by one would add the
    // attribute more than once or drop some. The fix is offered for one diagnostic at a time.
    public override FixAllProvider? GetFixAllProvider() => null;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var diagnostic = context.Diagnostics[0];
        if (diagnostic.Properties.GetValueOrDefault(JsonSerializableAnalyzer.TypeProperty) is not { } typeName)
        {
            return;
        }

        var compilation = await context.Document.Project.GetCompilationAsync(context.CancellationToken).ConfigureAwait(false);
        if (compilation is null)
        {
            return;
        }

        var solution = context.Document.Project.Solution;
        var shortName = typeName.Replace("global::", string.Empty);
        foreach (var declaration in JsonContexts.FindDeclarations(compilation, solution))
        {
            var contextName = declaration.Symbol.Name;
            context.RegisterCodeFix(
                CodeAction.Create(
                    $"Add [JsonSerializable(typeof({shortName}))] to {contextName}",
                    cancellationToken => AddAttributeAsync(solution, declaration.Reference, typeName, cancellationToken),
                    equivalenceKey: $"{nameof(AddJsonSerializableCodeFixProvider)}:{contextName}:{typeName}"),
                diagnostic);
        }
    }

    private static async Task<Solution> AddAttributeAsync(Solution solution, SyntaxReference reference, string typeName, CancellationToken cancellationToken)
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

        var edited = document.WithSyntaxRoot(root.ReplaceNode(declaration, WithAttribute(declaration, typeName)));

        // Shortens the qualified names written above to the ones the file can use, and adds the using directive they need.
        edited = await Simplifier.ReduceAsync(edited, cancellationToken: cancellationToken).ConfigureAwait(false);
        edited = await Formatter.FormatAsync(edited, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        return edited.Project.Solution;
    }

    private static ClassDeclarationSyntax WithAttribute(ClassDeclarationSyntax declaration, string typeName)
    {
        var attribute = SyntaxFactory.Attribute(
            SyntaxFactory.ParseName(AttributeName).WithAdditionalAnnotations(Simplifier.Annotation, Simplifier.AddImportsAnnotation),
            SyntaxFactory.AttributeArgumentList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.AttributeArgument(SyntaxFactory.TypeOfExpression(
                    SyntaxFactory.ParseTypeName(typeName).WithAdditionalAnnotations(Simplifier.Annotation, Simplifier.AddImportsAnnotation))))));
        var list = SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(attribute)).WithAdditionalAnnotations(Formatter.Annotation);

        var lists = declaration.AttributeLists;
        if (lists.Count == 0)
        {
            // The attribute becomes the first token of the declaration, so it takes over the comments above the class,
            // and the class keeps the indentation.
            var first = declaration.GetFirstToken();
            var indentation = first.LeadingTrivia.LastOrDefault(static trivia => trivia.IsKind(SyntaxKind.WhitespaceTrivia));
            list = list.WithLeadingTrivia(first.LeadingTrivia).WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);
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
