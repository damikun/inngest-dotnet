using Inngest.SourceGeneration.Tests.Support;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using StepNames = Inngest.SourceGeneration.Functions.FunctionRegistrationGenerator.StepNames;

namespace Inngest.SourceGeneration.Tests.Functions;

// The generator runs on every edit in an IDE, so an edit that changes nothing it registers must not run its steps again or
// write its output again. Roslyn calls the values of a step that came from the last run Cached, and the ones that were
// computed again and came out equal Unchanged.
public sealed class IncrementalityTests
{
    private const string Functions = """
        using System.Threading;
        using System.Threading.Tasks;
        using Inngest;

        namespace Shop
        {
            [InngestFunction("welcome")]
            public sealed class Welcome : InngestFunction<string>
            {
                public override Task<object?> ExecuteAsync(FunctionContext<string> context, CancellationToken cancellationToken) =>
                    Task.FromResult<object?>(null);
            }
        }
        """;

    private const string Unrelated = """
        namespace Shop
        {
            public sealed class Prices
            {
                public decimal Of(string sku) => 1m;
            }
        }
        """;

    [Fact]
    public void An_edit_to_a_file_without_functions_runs_nothing_again()
    {
        var edited = Unrelated.Replace("public decimal Of(string sku) => 1m;", "public decimal Of(string sku) => 2m;\n    public decimal Tax => 0.2m;", StringComparison.Ordinal);

        var result = RunThenEdit(Functions, Unrelated, edited, fileToEdit: 1);

        AssertNothingChanged(result);
    }

    [Fact]
    public void A_new_file_without_functions_runs_nothing_again()
    {
        var compilation = Generation.Compile("App", Functions, Unrelated);
        var driver = Generation.CreateDriver().RunGenerators(compilation);

        var withNewFile = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("namespace Shop { public sealed class Added; }", path: "Added.cs"));
        var result = driver.RunGenerators(withNewFile).GetRunResult();

        AssertNothingChanged(result);
    }

    [Fact]
    public void An_edit_inside_a_function_class_that_leaves_its_registration_as_it_was_writes_nothing_again()
    {
        var edited = Functions.Replace("Task.FromResult<object?>(null);", "Task.FromResult<object?>(\"edited\");", StringComparison.Ordinal);

        var result = RunThenEdit(Functions, Unrelated, edited, fileToEdit: 0);

        // The class was looked at again, because its file changed, and came out as it was, so nothing after it ran.
        Generation.Reasons(result, StepNames.AttributedClasses).ShouldBe([IncrementalStepRunReason.Unchanged]);
        Generation.Reasons(result, StepNames.FunctionClasses).ShouldBe([IncrementalStepRunReason.Cached]);
        AssertNothingChanged(result);
    }

    [Fact]
    public void A_new_function_class_writes_the_registration_again()
    {
        var edited = Functions.Replace(
            "}\n}",
            "}\n\n    [InngestFunction(\"goodbye\")]\n    public sealed class Goodbye : InngestFunction<string>\n    {\n        public override Task<object?> ExecuteAsync(FunctionContext<string> context, CancellationToken cancellationToken) =>\n            Task.FromResult<object?>(null);\n    }\n}",
            StringComparison.Ordinal);
        edited.ShouldContain("Goodbye");

        var result = RunThenEdit(Functions, Unrelated, edited, fileToEdit: 0);

        Generation.Reasons(result, StepNames.FunctionClasses).ShouldContain(IncrementalStepRunReason.New);
        Generation.Reasons(result, StepNames.Registration).ShouldBe([IncrementalStepRunReason.Modified]);
        Generation.OutputReasons(result).ShouldBe([IncrementalStepRunReason.Modified]);
        result.GeneratedTrees.Single().ToString().ShouldContain("Shop.Goodbye");
    }

    [Fact]
    public void Removing_the_attribute_writes_the_registration_again()
    {
        var edited = Functions.Replace("[InngestFunction(\"welcome\")]", string.Empty, StringComparison.Ordinal);

        var result = RunThenEdit(Functions, Unrelated, edited, fileToEdit: 0);

        // Nothing is registered, so nothing is written, and the output that was there is gone.
        Generation.Reasons(result, StepNames.Registration).ShouldBe([IncrementalStepRunReason.Modified]);
        result.GeneratedTrees.ShouldBeEmpty();
    }

    [Fact]
    public void Changing_a_property_of_the_project_writes_the_registration_again()
    {
        var compilation = Generation.Compile("App", Functions, Unrelated);
        var driver = Generation.CreateDriver().RunGenerators(compilation);
        driver.GetRunResult().GeneratedTrees.Single().ToString().ShouldContain("AddInngestFunctions");

        var changed = driver.WithUpdatedAnalyzerConfigOptions(Generation.Options(new Dictionary<string, string> { ["InngestFunctionsMethodName"] = "AddShopFunctions" }));
        var result = changed.RunGenerators(compilation).GetRunResult();

        Generation.Reasons(result, StepNames.Options).ShouldBe([IncrementalStepRunReason.Modified]);
        Generation.Reasons(result, StepNames.Registration).ShouldBe([IncrementalStepRunReason.Modified]);
        Generation.OutputReasons(result).ShouldBe([IncrementalStepRunReason.Modified]);
        result.GeneratedTrees.Single().ToString().ShouldContain("AddShopFunctions");
    }

    private static GeneratorDriverRunResult RunThenEdit(string functions, string unrelated, string edited, int fileToEdit)
    {
        var compilation = Generation.Compile("App", functions, unrelated);
        var driver = Generation.CreateDriver().RunGenerators(compilation);

        // The first run computes everything, so the second one shows what an edit costs.
        driver.GetRunResult().Results[0].TrackedSteps[StepNames.Registration].Single().Outputs.Single().Reason.ShouldBe(IncrementalStepRunReason.New);

        var editedTree = CSharpSyntaxTree.ParseText(edited, path: $"File{fileToEdit}.cs");
        var editedCompilation = compilation.ReplaceSyntaxTree(compilation.SyntaxTrees[fileToEdit], editedTree);
        return driver.RunGenerators(editedCompilation).GetRunResult();
    }

    private static void AssertNothingChanged(GeneratorDriverRunResult result)
    {
        IncrementalStepRunReason[] unchanged = [IncrementalStepRunReason.Cached, IncrementalStepRunReason.Unchanged];

        foreach (var step in new[] { StepNames.FunctionClasses, StepNames.AssemblyName, StepNames.Options, StepNames.Registration })
        {
            var reasons = Generation.Reasons(result, step);
            reasons.ShouldNotBeEmpty($"The step {step} did not run.");
            reasons.ShouldAllBe(reason => unchanged.Contains(reason), $"The step {step} produced something new: {string.Join(", ", reasons)}.");
        }

        var outputReasons = Generation.OutputReasons(result);
        outputReasons.ShouldNotBeEmpty();
        outputReasons.ShouldAllBe(reason => unchanged.Contains(reason), $"The output was written again: {string.Join(", ", outputReasons)}.");
        result.GeneratedTrees.Length.ShouldBe(1);
    }
}
