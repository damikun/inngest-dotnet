using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Inngest.SourceGeneration.Json;
using Inngest.SourceGeneration.Tests.Support;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Inngest.SourceGeneration.Tests.Json;

// Whether a type has metadata in a source-generated JsonSerializerContext is decided by the generator of System.Text.Json,
// and the analyzer has to say the same. A type it calls missing must fail at run time, and one it calls covered must not.
public sealed class JsonCoverageTests
{
    private const string GeneratorFile = "System.Text.Json.SourceGeneration.dll";

    [Fact]
    public void Covers_the_types_that_the_generator_of_System_Text_Json_writes_metadata_for()
    {
        var compilation = Generation.Compile("Probe", ProbeTypes.Source);

        var coverage = JsonCoverage.Create(compilation);

        var wrong = ProbeTypes.Expected
            .Where(pair => coverage.Covers(compilation.GetTypeByMetadataName("Probe." + pair.Key)!) != pair.Value)
            .Select(pair => $"{pair.Key} should be {(pair.Value ? "covered" : "missing")}")
            .ToList();
        wrong.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reports_the_types_that_no_context_reaches_and_only_those()
    {
        var calls = string.Join(
            "\n",
            ProbeTypes.Expected.Keys.Select((name, index) => $"        await context.Step.RunAsync<Probe.{name}>(\"step{index}\", Make<Probe.{name}>);"));
        var function = $$"""
            using System.Threading;
            using System.Threading.Tasks;
            using Inngest;

            namespace Probe.App;

            [InngestFunction("probe")]
            public sealed class ProbeFunction : InngestFunction<Probe.Root>
            {
                public override async Task<object?> ExecuteAsync(FunctionContext<Probe.Root> context, CancellationToken cancellationToken)
                {
            {{calls}}
                    return null;
                }

                private static T Make<T>() => default!;
            }
            """;
        var compilation = Generation.Compile("Probe", ProbeTypes.Source, function);
        var options = new AnalyzerOptions([], Generation.Options(new Dictionary<string, string> { ["PublishAot"] = "true" }));

        var diagnostics = await compilation.WithAnalyzers([new JsonSerializableAnalyzer()], options).GetAnalyzerDiagnosticsAsync();

        var missing = diagnostics
            .Where(diagnostic => diagnostic.Id == DiagnosticIds.TypeNotInJsonContext)
            .Select(diagnostic => diagnostic.GetMessage().Split('\'')[1].Replace("Probe.", string.Empty, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
        missing.ShouldBe(ProbeTypes.Expected.Where(pair => !pair.Value).Select(pair => pair.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Takes_from_the_context_of_the_SDK_the_types_that_context_has()
    {
        var contextType = typeof(IInngestBuilder).Assembly.GetType("Inngest.Json.PublicTypesJsonContext", throwOnError: true)!;
        var compilation = Generation.Compile("App", "public sealed class Placeholder;");

        var coverage = JsonCoverage.Create(compilation);

        // What the context has metadata for, by what the generator wrote: a JsonTypeInfo<T> property for each type.
        var has = contextType
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(property => property.PropertyType.IsGenericType && property.PropertyType.GetGenericTypeDefinition() == typeof(JsonTypeInfo<>))
            .Select(property => ToSymbol(property.PropertyType.GetGenericArguments()[0], compilation))
            .ToHashSet<ITypeSymbol>(SymbolEqualityComparer.Default);

        // The roots the analyzer starts from are the ones the context lists.
        var listed = contextType.GetCustomAttributesData()
            .Where(data => data.AttributeType == typeof(JsonSerializableAttribute))
            .Select(data => ToSymbol((Type)data.ConstructorArguments[0].Value!, compilation))
            .ToHashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        listed.ShouldNotBeEmpty();
        listed.ShouldAllBe(type => coverage.Covers(type), "The analyzer starts from a root that the SDK's context does not list or lacks one that it does.");

        var onlyInContext = has.Where(type => !coverage.Covers(type)).Select(type => type.ToDisplayString()).ToList();
        var onlyInAnalyzer = coverage.Types.Where(type => !has.Contains(type)).Select(type => type.ToDisplayString()).ToList();
        onlyInContext.ShouldBeEmpty("The context has metadata for these, which the analyzer would ask the app to list.");
        onlyInAnalyzer.ShouldBeEmpty("The analyzer takes these as covered, and the context has no metadata for them.");
    }

    public static IEnumerable<TheoryDataRow<string>> Generators() =>
        FindGenerators() is { Count: > 0 } found ? found.Select(path => new TheoryDataRow<string>(path)) : [new TheoryDataRow<string>(string.Empty)];

    [Theory]
    [MemberData(nameof(Generators))]
    public void Agrees_with_every_generator_of_System_Text_Json_it_can_find(string generatorPath)
    {
        if (generatorPath.Length == 0)
        {
            Assert.Skip("No targeting pack with the source generator of System.Text.Json was found next to this runtime or in the NuGet cache.");
        }

        var compilation = Generation.Compile("Probe", ProbeTypes.Source);
        var generatorContext = new AssemblyLoadContext("generator", isCollectible: true);
        var generator = (IIncrementalGenerator)Activator.CreateInstance(
            generatorContext.LoadFromAssemblyPath(generatorPath).GetType("System.Text.Json.SourceGeneration.JsonSourceGenerator", throwOnError: true)!)!;

        CSharpGeneratorDriver.Create(generator).RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        diagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();

        using var stream = new MemoryStream();
        var emitted = output.Emit(stream);
        emitted.Success.ShouldBeTrue(string.Join(Environment.NewLine, emitted.Diagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
        stream.Position = 0;
        var assembly = new AssemblyLoadContext("probe", isCollectible: true).LoadFromStream(stream);
        var context = (JsonSerializerContext)assembly.GetType("Probe.Ctx", throwOnError: true)!.GetProperty("Default")!.GetValue(null)!;

        var coverage = JsonCoverage.Create(compilation);
        var disagreements = new List<string>();
        foreach (var (name, expected) in ProbeTypes.Expected)
        {
            var written = context.GetTypeInfo(assembly.GetType("Probe." + name, throwOnError: true)!) is not null;
            var analyzed = coverage.Covers(compilation.GetTypeByMetadataName("Probe." + name)!);
            if (written != analyzed || written != expected)
            {
                disagreements.Add($"{name}: the generator {(written ? "wrote" : "did not write")} metadata, the analyzer says {(analyzed ? "covered" : "missing")}, the test expects {(expected ? "covered" : "missing")}");
            }
        }

        disagreements.ShouldBeEmpty();
    }

    // The generator of System.Text.Json comes with the targeting pack of each framework, in the folder of the SDK and, once
    // a project has been built for it, in the NuGet cache. Both are searched; an older one is as good as a newer one, and
    // this should hold for each.
    private static List<string> FindGenerators()
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        var dotnetRoot = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        var packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        foreach (var root in new[] { Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref"), Path.Combine(packages, "microsoft.netcore.app.ref") })
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var directory in Directory.GetDirectories(root))
            {
                var path = Path.Combine(directory, "analyzers", "dotnet", "cs", GeneratorFile);
                var major = Path.GetFileName(directory).Split('.')[0];
                if (File.Exists(path))
                {
                    found.TryAdd(major, path);
                }
            }
        }

        return [.. found.Values];
    }

    private static ITypeSymbol ToSymbol(Type type, Compilation compilation)
    {
        if (type.IsArray)
        {
            return compilation.CreateArrayTypeSymbol(ToSymbol(type.GetElementType()!, compilation));
        }

        if (type.IsGenericType)
        {
            return compilation.GetTypeByMetadataName(type.GetGenericTypeDefinition().FullName!)!
                .Construct([.. type.GetGenericArguments().Select(argument => ToSymbol(argument, compilation))]);
        }

        return compilation.GetTypeByMetadataName(type.FullName!)!;
    }
}
