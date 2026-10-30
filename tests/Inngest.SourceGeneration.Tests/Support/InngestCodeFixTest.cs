using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;

namespace Inngest.SourceGeneration.Tests.Support;

// A test of a code fix on a project that references Inngest and publishes with Native AOT.
internal sealed class InngestCodeFixTest<TAnalyzer, TCodeFix> : CSharpCodeFixTest<TAnalyzer, TCodeFix, DefaultVerifier>
    where TAnalyzer : DiagnosticAnalyzer, new()
    where TCodeFix : CodeFixProvider, new()
{
    public InngestCodeFixTest(string source, string fixedSource, params (string Name, string Source)[] otherFiles)
    {
        ProjectSetup.Configure(this, Sources.PublishAot);
        TestState.Sources.Add(source);
        FixedState.Sources.Add(fixedSource);
        foreach (var (name, other) in otherFiles)
        {
            TestState.Sources.Add((name, other));
            FixedState.Sources.Add((name, other));
        }
    }
}
