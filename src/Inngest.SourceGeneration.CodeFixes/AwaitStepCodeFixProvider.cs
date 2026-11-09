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
using Microsoft.CodeAnalysis.Simplification;

namespace Inngest.SourceGeneration;

/// <summary>
/// Awaits the task of a step instead of blocking on it, in a function that is async already.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AwaitStepCodeFixProvider))]
[Shared]
public sealed class AwaitStepCodeFixProvider : CodeFixProvider
{
    private const string Title = "Await the step";

    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create(DiagnosticIds.BlockingOnStep);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var diagnostic = context.Diagnostics[0];
        if (!diagnostic.Properties.ContainsKey(StepCallAnalyzer.CanAwaitProperty))
        {
            return;
        }

        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root?.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true) is not ExpressionSyntax blocking
            || TaskOf(blocking) is not { } task)
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(Title, cancellationToken => AwaitAsync(context.Document, root, blocking, task, cancellationToken), equivalenceKey: Title),
            diagnostic);
    }

    // The task in task.Result, task.Wait() and task.GetAwaiter().GetResult().
    private static ExpressionSyntax? TaskOf(ExpressionSyntax blocking) => blocking switch
    {
        MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Result" } result => result.Expression,
        InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Wait" } wait } => wait.Expression,
        InvocationExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText: "GetResult",
                Expression: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "GetAwaiter" } awaiter },
            },
        } => awaiter.Expression,
        _ => null,
    };

    private static Task<Document> AwaitAsync(Document document, SyntaxNode root, ExpressionSyntax blocking, ExpressionSyntax task, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var awaited = SyntaxFactory.ParenthesizedExpression(SyntaxFactory.AwaitExpression(task.WithoutTrivia()))
            .WithTriviaFrom(blocking)
            .WithAdditionalAnnotations(Simplifier.Annotation);
        return Task.FromResult(document.WithSyntaxRoot(root.ReplaceNode(blocking, awaited)));
    }
}
