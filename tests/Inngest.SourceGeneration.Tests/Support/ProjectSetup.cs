using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Testing;

namespace Inngest.SourceGeneration.Tests.Support;

internal static class ProjectSetup
{
    // Makes the project of a test one that references Inngest, with nullable reference types on as in the apps this package
    // is for. The properties are the MSBuild properties the compiler would be given, as the text of a .globalconfig.
    public static void Configure<TVerifier>(AnalyzerTest<TVerifier> test, string? properties)
        where TVerifier : IVerifier, new()
    {
        test.ReferenceAssemblies = TestReferences.Framework;
        test.TestState.AdditionalReferences.AddRange(TestReferences.Libraries);

        if (properties is not null)
        {
            test.TestState.AnalyzerConfigFiles.Add(("/.globalconfig", properties));
        }

        test.SolutionTransforms.Add((solution, projectId) =>
        {
            var options = (CSharpCompilationOptions)solution.GetProject(projectId)!.CompilationOptions!;
            return solution.WithProjectCompilationOptions(projectId, options.WithNullableContextOptions(NullableContextOptions.Enable));
        });
    }
}
