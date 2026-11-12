// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Inngest.SourceGeneration.Steps;

// Whether two pieces of code are in different branches of one if, switch or conditional expression, so that a call
// of the function runs at most one of them.
internal static class Branches
{
    public static bool AreExclusive(SyntaxNode first, SyntaxNode second)
    {
        var ancestors = new HashSet<SyntaxNode>(first.AncestorsAndSelf());
        SyntaxNode? fromSecond = null;
        foreach (var node in second.AncestorsAndSelf())
        {
            if (ancestors.Contains(node))
            {
                var fromFirst = ChildOnPath(first, node);
                return fromFirst is not null && fromSecond is not null && AreBranches(node, fromFirst, fromSecond);
            }

            fromSecond = node;
        }

        return false;
    }

    // The child of the ancestor that the node is in.
    private static SyntaxNode? ChildOnPath(SyntaxNode node, SyntaxNode ancestor)
    {
        SyntaxNode? child = null;
        for (var current = node; current is not null && current != ancestor; current = current.Parent)
        {
            child = current;
        }

        return child;
    }

    private static bool AreBranches(SyntaxNode parent, SyntaxNode one, SyntaxNode other) => parent switch
    {
        IfStatementSyntax @if => (one == @if.Statement && other == @if.Else) || (one == @if.Else && other == @if.Statement),
        ConditionalExpressionSyntax conditional =>
            (one == conditional.WhenTrue && other == conditional.WhenFalse) || (one == conditional.WhenFalse && other == conditional.WhenTrue),
        SwitchStatementSyntax => one is SwitchSectionSyntax && other is SwitchSectionSyntax && one != other,
        SwitchExpressionSyntax => one is SwitchExpressionArmSyntax && other is SwitchExpressionArmSyntax && one != other,
        _ => false,
    };
}
