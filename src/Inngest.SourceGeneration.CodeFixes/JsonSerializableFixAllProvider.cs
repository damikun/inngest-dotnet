// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;

namespace Inngest.SourceGeneration.Json;

// Fixing the diagnostics of a document or a project one by one would edit the same class once for each of them, and the
// edits of the standard providers, which merge text changes, collide there. This adds every missing type to the context in
// a single edit, each type once however many diagnostics name it. In a solution each project gets the types of its own
// diagnostics, on its context of that name.
internal sealed class JsonSerializableFixAllProvider : FixAllProvider
{
    public static JsonSerializableFixAllProvider Instance { get; } = new();

    public override IEnumerable<FixAllScope> GetSupportedFixAllScopes() => [FixAllScope.Document, FixAllScope.Project, FixAllScope.Solution];

    public override async Task<CodeAction?> GetFixAsync(FixAllContext fixAllContext)
    {
        var key = fixAllContext.CodeActionEquivalenceKey;
        if (key is null || !key.StartsWith(AddJsonSerializableCodeFixProvider.EquivalenceKeyPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var contextName = key.Substring(AddJsonSerializableCodeFixProvider.EquivalenceKeyPrefix.Length);
        IEnumerable<Project> projects = fixAllContext.Scope == FixAllScope.Solution ? fixAllContext.Solution.Projects : [fixAllContext.Project];

        var edits = new List<(SyntaxReference Context, List<string> TypeNames)>();
        foreach (var project in projects)
        {
            var typeNames = await MissingTypesAsync(fixAllContext, project).ConfigureAwait(false);
            if (typeNames.Count == 0)
            {
                continue;
            }

            var compilation = await project.GetCompilationAsync(fixAllContext.CancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            foreach (var declaration in JsonContexts.FindDeclarations(compilation, project.Solution))
            {
                if (declaration.Symbol.ToDisplayString() == contextName)
                {
                    edits.Add((declaration.Reference, typeNames));
                    break;
                }
            }
        }

        if (edits.Count == 0)
        {
            return null;
        }

        var solution = fixAllContext.Solution;
        var count = edits.Sum(static edit => edit.TypeNames.Count);
        return CodeAction.Create(
            $"Add {count} {(count == 1 ? "type" : "types")} to the JsonSerializerContext",
            async cancellationToken =>
            {
                var edited = solution;
                foreach (var (context, typeNames) in edits)
                {
                    edited = await JsonSerializableEdit.AddAttributesAsync(edited, context, typeNames, cancellationToken).ConfigureAwait(false);
                }

                return edited;
            },
            equivalenceKey: key);
    }

    // The types of the diagnostics in the scope, in the order the diagnostics are in the code.
    private static async Task<List<string>> MissingTypesAsync(FixAllContext fixAllContext, Project project)
    {
        var diagnostics = fixAllContext.Scope == FixAllScope.Document && fixAllContext.Document is { } document
            ? await fixAllContext.GetDocumentDiagnosticsAsync(document).ConfigureAwait(false)
            : await fixAllContext.GetAllDiagnosticsAsync(project).ConfigureAwait(false);

        var typeNames = new List<string>();
        foreach (var diagnostic in diagnostics.OrderBy(static d => d.Location.SourceTree?.FilePath, StringComparer.Ordinal).ThenBy(static d => d.Location.SourceSpan.Start))
        {
            if (diagnostic.Properties.GetValueOrDefault(JsonSerializableAnalyzer.TypeProperty) is { } typeName && !typeNames.Contains(typeName))
            {
                typeNames.Add(typeName);
            }
        }

        return typeNames;
    }
}
