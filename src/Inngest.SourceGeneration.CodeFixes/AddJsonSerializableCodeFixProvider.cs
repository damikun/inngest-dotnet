// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Collections.Immutable;
using System.Composition;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;

namespace Inngest.SourceGeneration.Json;

/// <summary>
/// Lists the missing type on a JsonSerializerContext of the project with [JsonSerializable(typeof(...))], one fix for each
/// context there is.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AddJsonSerializableCodeFixProvider))]
[Shared]
public sealed class AddJsonSerializableCodeFixProvider : CodeFixProvider
{
    // The key names the context and not the type, so that fixing all diagnostics of one key adds every type to that context.
    internal const string EquivalenceKeyPrefix = nameof(AddJsonSerializableCodeFixProvider) + ":";

    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create(DiagnosticIds.TypeNotInJsonContext);

    public override FixAllProvider GetFixAllProvider() => JsonSerializableFixAllProvider.Instance;

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
            context.RegisterCodeFix(
                CodeAction.Create(
                    $"Add [JsonSerializable(typeof({shortName}))] to {declaration.Symbol.Name}",
                    cancellationToken => JsonSerializableEdit.AddAttributesAsync(solution, declaration.Reference, [typeName], cancellationToken),
                    equivalenceKey: EquivalenceKeyPrefix + declaration.Symbol.ToDisplayString()),
                diagnostic);
        }
    }
}
