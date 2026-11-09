// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Inngest.SourceGeneration.Steps;

/// <summary>
/// Points out a finally block, a using statement or a using declaration that encloses a step: a call that stops at a
/// new step abandons the function without running them. It is information only, since the cleanup often does no harm
/// when it runs late, as with a CancellationTokenSource.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StepCleanupAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(DiagnosticDescriptors.CleanupAroundStep);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(
            AnalyzeNode,
            SyntaxKind.TryStatement,
            SyntaxKind.UsingStatement,
            SyntaxKind.LocalDeclarationStatement);
    }

    private static void AnalyzeNode(SyntaxNodeAnalysisContext context)
    {
        var (keyword, kind, region) = context.Node switch
        {
            TryStatementSyntax { Finally: { } @finally } @try => (@finally.FinallyKeyword, "finally block", Protected(@try)),
            UsingStatementSyntax @using => (@using.UsingKeyword, "using statement", ImmutableArray.Create<SyntaxNode>(@using.Statement)),
            LocalDeclarationStatementSyntax { UsingKeyword.RawKind: (int)SyntaxKind.UsingKeyword } declaration =>
                (declaration.UsingKeyword, "using declaration", Following(declaration)),
            _ => (default, string.Empty, ImmutableArray<SyntaxNode>.Empty),
        };

        if (region.IsEmpty || FirstStep(context, region) is not { } step)
        {
            return;
        }

        // Inside a step's handler the step it encloses is nested, which INNGEST024 reports.
        if (context.SemanticModel.GetOperation(context.Node, context.CancellationToken) is { } operation && StepCalls.EnclosingStep(operation) is not null)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.CleanupAroundStep, keyword.GetLocation(), kind, StepCalls.Describe(step)));
    }

    // The try block and the catch blocks: a step in a catch block abandons the function before the finally block too.
    private static ImmutableArray<SyntaxNode> Protected(TryStatementSyntax @try)
    {
        var builder = ImmutableArray.CreateBuilder<SyntaxNode>();
        builder.Add(@try.Block);
        foreach (var @catch in @try.Catches)
        {
            builder.Add(@catch.Block);
        }

        return builder.ToImmutable();
    }

    // A using declaration disposes at the end of its block, so it encloses the statements after it.
    private static ImmutableArray<SyntaxNode> Following(LocalDeclarationStatementSyntax declaration)
    {
        if (declaration.Parent is not BlockSyntax block)
        {
            return ImmutableArray<SyntaxNode>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<SyntaxNode>();
        var after = false;
        foreach (var statement in block.Statements)
        {
            if (after)
            {
                builder.Add(statement);
            }

            after |= statement == declaration;
        }

        return builder.ToImmutable();
    }

    private static IInvocationOperation? FirstStep(SyntaxNodeAnalysisContext context, ImmutableArray<SyntaxNode> region)
    {
        foreach (var node in region)
        {
            foreach (var invocation in node.DescendantNodesAndSelf(RunsInline).OfType<InvocationExpressionSyntax>())
            {
                if (context.SemanticModel.GetOperation(invocation, context.CancellationToken) is IInvocationOperation operation
                    && StepCalls.IsStep(operation.TargetMethod))
                {
                    return operation;
                }
            }
        }

        return null;
    }

    // Code that runs where it is written. A lambda or a local function runs when it is called, which may be anywhere,
    // as with the handler of a durable endpoint mapped after a using declaration, so their bodies are left out. The
    // callback of ParallelAsync runs in place; the handler of a step runs inside the step, and a step there is nested.
    private static bool RunsInline(SyntaxNode node) => node switch
    {
        LocalFunctionStatementSyntax => false,
        AnonymousFunctionExpressionSyntax lambda => IsParallelCallback(lambda),
        _ => true,
    };

    private static bool IsParallelCallback(AnonymousFunctionExpressionSyntax lambda) =>
        lambda.Parent is ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax { Expression: var expression } }
        && expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
            _ => null,
        } == StepCalls.Parallel;
}
