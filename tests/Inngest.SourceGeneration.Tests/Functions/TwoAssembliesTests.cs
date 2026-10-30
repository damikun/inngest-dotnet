using Inngest.SourceGeneration.Tests.Support;
using Microsoft.CodeAnalysis;

namespace Inngest.SourceGeneration.Tests.Functions;

// An app can have function classes in more than one assembly, and each assembly that references Inngest gets a generated
// method of its own. These build a library and the app that references it, with the generator on both.
public sealed class TwoAssembliesTests
{
    private const string Library = """
        using System.Threading;
        using System.Threading.Tasks;
        using Inngest;

        namespace Shop.Billing
        {
            [InngestFunction("invoice")]
            public sealed class InvoiceFunction : InngestFunction<string>
            {
                public override Task<object?> ExecuteAsync(FunctionContext<string> context, CancellationToken cancellationToken) => Task.FromResult<object?>(null);
            }
        }
        """;

    private const string App = """
        using System.Threading;
        using System.Threading.Tasks;
        using Inngest;

        namespace Shop.Api
        {
            [InngestFunction("welcome")]
            public sealed class WelcomeFunction : InngestFunction<string>
            {
                public override Task<object?> ExecuteAsync(FunctionContext<string> context, CancellationToken cancellationToken) => Task.FromResult<object?>(null);
            }

            public static class Startup
            {
                public static IInngestBuilder Register(IInngestBuilder builder) => builder.{CALLS};
            }
        }
        """;

    [Fact]
    public void A_library_shares_its_functions_with_a_public_method_of_its_own_name()
    {
        var library = Build("Shop.Billing", Library, new Dictionary<string, string>
        {
            ["InngestFunctionsMethodName"] = "AddBillingFunctions",
            ["InngestFunctionsAccessibility"] = "public",
        });

        // The app registers its own functions with its method and the library's with the library's.
        ErrorsOf("Shop.Api", App.Replace("{CALLS}", "AddInngestFunctions().AddBillingFunctions()", StringComparison.Ordinal), library).ShouldBeEmpty();
    }

    [Fact]
    public void The_default_method_of_a_library_is_internal_so_it_does_not_meet_the_one_of_the_app()
    {
        var library = Build("Shop.Billing", Library, properties: null);

        ErrorsOf("Shop.Api", App.Replace("{CALLS}", "AddInngestFunctions()", StringComparison.Ordinal), library).ShouldBeEmpty();
    }

    [Fact]
    public void A_library_that_does_not_share_its_method_gives_the_app_nothing_to_call()
    {
        var library = Build("Shop.Billing", Library, properties: null);

        var errors = ErrorsOf("Shop.Api", App.Replace("{CALLS}", "AddInngestFunctions().AddBillingFunctions()", StringComparison.Ordinal), library);

        errors.ShouldHaveSingleItem().ShouldContain("CS1061");
    }

    [Fact]
    public void Two_public_methods_of_the_same_name_are_ambiguous_and_the_compiler_says_so()
    {
        // Only a method name of its own makes a library's method public safely.
        var library = Build("Shop.Billing", Library, new Dictionary<string, string> { ["InngestFunctionsAccessibility"] = "public" });

        var errors = ErrorsOf("Shop.Api", App.Replace("{CALLS}", "AddInngestFunctions()", StringComparison.Ordinal), library);

        errors.ShouldHaveSingleItem().ShouldContain("CS0121");
    }

    private static MetadataReference Build(string assemblyName, string source, IReadOnlyDictionary<string, string>? properties)
    {
        var compilation = Generation.Compile(assemblyName, source);
        Generation.CreateDriver(properties).RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        diagnostics.ShouldBeEmpty();

        var stream = new MemoryStream();
        var emitted = output.Emit(stream);
        emitted.Success.ShouldBeTrue(string.Join(Environment.NewLine, emitted.Diagnostics));
        stream.Position = 0;
        return MetadataReference.CreateFromStream(stream);
    }

    private static List<string> ErrorsOf(string assemblyName, string source, MetadataReference library)
    {
        var compilation = Generation.Compile(assemblyName, source).AddReferences(library);
        Generation.CreateDriver().RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        return [.. output.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Select(static diagnostic => diagnostic.ToString())];
    }
}
