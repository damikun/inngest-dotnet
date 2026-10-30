using Basic.Reference.Assemblies;
using Inngest.SourceGeneration.Functions;
using Inngest.SourceGeneration.Tests.Support;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Inngest.SourceGeneration.Tests.Functions;

// The generator finds the attribute by its name, so it must not act on an app's own type of the same name.
public sealed class LookalikeTypesTests
{
    private const string Source = """
        using System;

        namespace Inngest
        {
            [AttributeUsage(AttributeTargets.Class)]
            public sealed class InngestFunctionAttribute(string id) : Attribute;

            public abstract class InngestFunction;
        }

        namespace Shop
        {
            [Inngest.InngestFunction("lookalike")]
            public sealed class Lookalike : Inngest.InngestFunction
            {
            }
        }
        """;

    [Fact]
    public void The_generator_ignores_classes_of_another_assembly_that_look_like_a_function()
    {
        var compilation = CSharpCompilation.Create("App", [CSharpSyntaxTree.ParseText(Source)], Net100.References.All, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var result = Generation.CreateDriver().RunGenerators(compilation).GetRunResult();

        result.GeneratedTrees.ShouldBeEmpty();
        result.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_analyzer_ignores_them_too()
    {
        var compilation = CSharpCompilation.Create("App", [CSharpSyntaxTree.ParseText(Source)], Net100.References.All, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var diagnostics = await compilation.WithAnalyzers([new FunctionClassAnalyzer()]).GetAnalyzerDiagnosticsAsync();

        diagnostics.ShouldBeEmpty();
    }
}
