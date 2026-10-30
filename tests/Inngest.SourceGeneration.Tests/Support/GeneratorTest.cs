using System.Text;
using Inngest.SourceGeneration.Functions;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Text;

namespace Inngest.SourceGeneration.Tests.Support;

// A test of the registration generator on a project that references Inngest. The generated file is the one thing it can
// expect, written out in full so that the test shows what a project gets.
internal sealed class GeneratorTest : CSharpSourceGeneratorTest<FunctionRegistrationGenerator, DefaultVerifier>
{
    // What the generator writes for the version of the assembly that is running.
    private static readonly string GeneratorVersion = typeof(FunctionRegistrationGenerator).Assembly.GetName().Version!.ToString();

    public GeneratorTest(string source, string? generated = null, string? properties = null, params string[] otherSources)
    {
        ProjectSetup.Configure(this, properties is null ? null : "is_global = true\n" + properties);
        TestState.Sources.Add(source);
        foreach (var other in otherSources)
        {
            TestState.Sources.Add(other);
        }

        if (generated is not null)
        {
            TestState.GeneratedSources.Add((typeof(FunctionRegistrationGenerator), FunctionRegistrationEmitter.HintName, SourceText.From(Expected(generated), Encoding.UTF8)));
        }
    }

    // The expectations are written with {version} where the generator writes its own, which changes with every release.
    private static string Expected(string generated) => generated.Replace("{version}", GeneratorVersion, StringComparison.Ordinal);
}
