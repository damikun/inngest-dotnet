using System.Collections.Immutable;
using Inngest.SourceGeneration.Functions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Inngest.SourceGeneration.Tests.Support;

// Runs the generator by hand, for the tests that need more than one run of it or the compiled result of its output.
internal static class Generation
{
    public static CSharpCompilation Compile(string assemblyName, params string[] sources) =>
        CSharpCompilation.Create(
            assemblyName,
            sources.Select((source, index) => CSharpSyntaxTree.ParseText(source, path: $"File{index}.cs")),
            TestReferences.Libraries,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    // Tracking records what ran again on each run, so that a test can look at it.
    public static GeneratorDriver CreateDriver(IReadOnlyDictionary<string, string>? properties = null) =>
        CSharpGeneratorDriver.Create(
            generators: [new FunctionRegistrationGenerator().AsSourceGenerator()],
            additionalTexts: [],
            parseOptions: null,
            optionsProvider: Options(properties ?? new Dictionary<string, string>()),
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

    // The MSBuild properties of a project, as the compiler passes them to a generator.
    public static AnalyzerConfigOptionsProvider Options(IReadOnlyDictionary<string, string> properties) => new TestOptionsProvider(properties);

    // The values the step with this name produced on the last run, and why each was produced: the same as before, rerun
    // with an equal result, or new.
    public static ImmutableArray<IncrementalStepRunReason> Reasons(GeneratorDriverRunResult result, string stepName) =>
        [.. result.Results[0].TrackedSteps[stepName].SelectMany(static step => step.Outputs).Select(static output => output.Reason)];

    public static ImmutableArray<IncrementalStepRunReason> OutputReasons(GeneratorDriverRunResult result) =>
        [.. result.Results[0].TrackedOutputSteps.SelectMany(static pair => pair.Value).SelectMany(static step => step.Outputs).Select(static output => output.Reason)];

    private sealed class TestOptionsProvider(IReadOnlyDictionary<string, string> properties) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new TestOptions(properties.ToDictionary(static pair => "build_property." + pair.Key, static pair => pair.Value));

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => TestOptions.Empty;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => TestOptions.Empty;
    }

    private sealed class TestOptions(Dictionary<string, string> values) : AnalyzerConfigOptions
    {
        public static TestOptions Empty { get; } = new([]);

        public override bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value) => values.TryGetValue(key, out value);
    }
}
