// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Collections.Immutable;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Inngest.SourceGeneration.Steps;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Inngest.SourceGeneration;

/// <summary>
/// Gets a value from the clock or a random source in a step, so that every call of the function uses the value of the
/// first: DateTime.UtcNow becomes (await context.Step.RunAsync("utc-now", () => DateTime.UtcNow)).
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(RunInStepCodeFixProvider))]
[Shared]
public sealed class RunInStepCodeFixProvider : CodeFixProvider
{
    private const string Title = "Get the value in a step";

    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create(DiagnosticIds.NondeterministicValue);

    // Each fix picks an id that the member does not use yet, which fixes applied together would each pick alike.
    public override FixAllProvider? GetFixAllProvider() => null;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var diagnostic = context.Diagnostics[0];
        if (!diagnostic.Properties.TryGetValue(FunctionBodyAnalyzer.ContextProperty, out var functionContext) || functionContext is null
            || !diagnostic.Properties.TryGetValue(FunctionBodyAnalyzer.StepIdProperty, out var id) || id is null)
        {
            return;
        }

        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root?.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true) is not ExpressionSyntax value)
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(Title, cancellationToken => WrapAsync(context.Document, root, value, functionContext, id, cancellationToken), equivalenceKey: Title),
            diagnostic);
    }

    private static Task<Document> WrapAsync(Document document, SyntaxNode root, ExpressionSyntax value, string context, string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var handler = SyntaxFactory.ParenthesizedLambdaExpression(value.WithoutTrivia());
        var step = StepIds.Call(context, "RunAsync", StepIds.Unique(value, id), handler);
        return Task.FromResult(document.WithSyntaxRoot(root.ReplaceNode(value, StepIds.Awaited(step).WithTriviaFrom(value))));
    }
}
