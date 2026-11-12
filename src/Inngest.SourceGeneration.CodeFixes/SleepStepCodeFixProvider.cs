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
/// Waits with a sleep step instead of Task.Delay or Thread.Sleep: await Task.Delay(5000) becomes
/// await context.Step.SleepAsync("sleep", TimeSpan.FromSeconds(5)). Inngest sleeps with one-second resolution, so a
/// shorter wait becomes a second.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(SleepStepCodeFixProvider))]
[Shared]
public sealed class SleepStepCodeFixProvider : CodeFixProvider
{
    private const string Title = "Sleep in a step";

    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create(DiagnosticIds.DelayInFunction);

    // Each fix picks an id that the member does not use yet, which fixes applied together would each pick alike.
    public override FixAllProvider? GetFixAllProvider() => null;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var diagnostic = context.Diagnostics[0];
        if (!diagnostic.Properties.TryGetValue(FunctionBodyAnalyzer.ContextProperty, out var functionContext) || functionContext is null)
        {
            return;
        }

        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (root?.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true)?.FirstAncestorOrSelf<InvocationExpressionSyntax>() is not { } call
            || model is null
            || call.ArgumentList.Arguments.Count == 0)
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(Title, cancellationToken => SleepAsync(context.Document, root, model, call, functionContext, cancellationToken), equivalenceKey: Title),
            diagnostic);
    }

    private static Task<Document> SleepAsync(Document document, SyntaxNode root, SemanticModel model, InvocationExpressionSyntax call, string context, CancellationToken cancellationToken)
    {
        var step = StepIds.Call(context, "SleepAsync", StepIds.Unique(call, "sleep"), Duration(model, call.ArgumentList.Arguments[0].Expression, cancellationToken));

        // Task.Delay is awaited already; Thread.Sleep is a statement of its own, which now awaits the step.
        ExpressionSyntax replacement = IsThreadSleep(model, call, cancellationToken) ? SyntaxFactory.AwaitExpression(step) : step;
        return Task.FromResult(document.WithSyntaxRoot(root.ReplaceNode(call, replacement.WithTriviaFrom(call))));
    }

    // A TimeSpan as it is, and milliseconds as TimeSpan.FromSeconds when they are whole seconds, else FromMilliseconds.
    private static ExpressionSyntax Duration(SemanticModel model, ExpressionSyntax argument, CancellationToken cancellationToken)
    {
        var type = model.GetTypeInfo(argument, cancellationToken).Type;
        if (type is { Name: "TimeSpan" })
        {
            return argument.WithoutTrivia();
        }

        var constant = model.GetConstantValue(argument, cancellationToken);
        var (method, value) = constant is { HasValue: true, Value: int milliseconds } && milliseconds > 0 && milliseconds % 1000 == 0
            ? ("FromSeconds", (ExpressionSyntax)SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(milliseconds / 1000)))
            : ("FromMilliseconds", argument.WithoutTrivia());

        return SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.ParseExpression("System.TimeSpan").WithAdditionalAnnotations(Simplifier.Annotation), SyntaxFactory.IdentifierName(method)),
                SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(value))))
            .WithAdditionalAnnotations(Simplifier.Annotation);
    }

    private static bool IsThreadSleep(SemanticModel model, InvocationExpressionSyntax call, CancellationToken cancellationToken) =>
        model.GetSymbolInfo(call, cancellationToken).Symbol is IMethodSymbol { Name: "Sleep", ContainingType.Name: "Thread" };
}
