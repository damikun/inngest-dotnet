// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Simplification;

namespace Inngest.SourceGeneration;

// What the code fixes that add a step write: an id that no string in the member uses yet, so that the new step does
// not share an id with another one (INNGEST032), and the call on the step tools of the function's context.
internal static class StepIds
{
    public static string Unique(SyntaxNode node, string id)
    {
        var scope = node.FirstAncestorOrSelf<MemberDeclarationSyntax>() ?? node.SyntaxTree.GetRoot();
        var taken = new HashSet<string>(
            scope.DescendantTokens().Where(static token => token.IsKind(SyntaxKind.StringLiteralToken)).Select(static token => token.ValueText));

        var candidate = id;
        for (var index = 2; taken.Contains(candidate); index++)
        {
            candidate = id + "-" + index.ToString(CultureInfo.InvariantCulture);
        }

        return candidate;
    }

    // context.Step.Method("id", arguments...).
    public static InvocationExpressionSyntax Call(string context, string method, string id, params ExpressionSyntax[] arguments) =>
        SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName(context), SyntaxFactory.IdentifierName("Step")),
                SyntaxFactory.IdentifierName(method)),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
                new[] { SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(id))) }
                    .Concat(arguments.Select(SyntaxFactory.Argument)))));

    // (await task), whose parentheses the simplifier removes where they are not needed.
    public static ExpressionSyntax Awaited(ExpressionSyntax task) =>
        SyntaxFactory.ParenthesizedExpression(SyntaxFactory.AwaitExpression(task)).WithAdditionalAnnotations(Simplifier.Annotation);
}
