using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;

namespace Inngest.SourceGeneration.Tests.Support;

// A test of an analyzer on a project that references Inngest. The code marks where the diagnostics are with {|ID:...|}.
internal sealed class InngestAnalyzerTest<TAnalyzer> : CSharpAnalyzerTest<TAnalyzer, DefaultVerifier>
    where TAnalyzer : DiagnosticAnalyzer, new()
{
    public InngestAnalyzerTest(string source, string? properties = null, params string[] otherSources)
    {
        ProjectSetup.Configure(this, properties);
        TestState.Sources.Add(source);
        foreach (var other in otherSources)
        {
            TestState.Sources.Add(other);
        }
    }
}
