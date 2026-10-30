using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;

namespace Inngest.SourceGeneration.Tests.Support;

// A test of a code fix on a project that references Inngest and publishes with Native AOT. The tests add the files of the
// project to TestState and the files as they should be after the fix to FixedState.
internal sealed class InngestCodeFixTest<TAnalyzer, TCodeFix> : CSharpCodeFixTest<TAnalyzer, TCodeFix, DefaultVerifier>
    where TAnalyzer : DiagnosticAnalyzer, new()
    where TCodeFix : CodeFixProvider, new()
{
    public InngestCodeFixTest()
    {
        ProjectSetup.Configure(this, Sources.PublishAot);
    }

    // A file that the fix does not change.
    public void AddUnchanged(string name, string source)
    {
        TestState.Sources.Add((name, source));
        FixedState.Sources.Add((name, source));
    }
}
