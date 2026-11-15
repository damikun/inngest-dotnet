using Inngest.SourceGeneration.Steps;
using Inngest.SourceGeneration.Tests.Support;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;

namespace Inngest.SourceGeneration.Tests.Steps;

public sealed class AwaitStepCodeFixTests
{
    [Fact]
    public async Task Awaits_the_step_in_an_async_function()
    {
        const string source = """
            public static class Blocking
            {
                public static async Task<int> Run(IStepTools step)
                {
                    var a = {|INNGEST023:step.RunAsync("a", () => 1).Result|};
                    {|INNGEST023:step.SleepAsync("b", TimeSpan.FromSeconds(1)).Wait()|};
                    var c = {|INNGEST023:step.RunAsync("c", () => 3).GetAwaiter().GetResult()|} + 1;
                    return a + c;
                }
            }
            """;
        const string fixedSource = """
            public static class Blocking
            {
                public static async Task<int> Run(IStepTools step)
                {
                    var a = await step.RunAsync("a", () => 1);
                    await step.SleepAsync("b", TimeSpan.FromSeconds(1));
                    var c = await step.RunAsync("c", () => 3) + 1;
                    return a + c;
                }
            }
            """;

        await Run(source, fixedSource);
    }

    [Fact]
    public async Task Offers_nothing_where_the_function_is_not_async()
    {
        const string source = """
            public static class Blocking
            {
                public static int Run(IStepTools step) => {|INNGEST023:step.RunAsync("a", () => 1).Result|};
            }
            """;

        await Run(source, source);
    }

    [Fact]
    public async Task Awaits_a_task_the_function_starts()
    {
        const string source = """
            [InngestFunction("order")]
            public sealed class OnOrder : InngestFunction<JsonElement>
            {
                public override async Task<object?> ExecuteAsync(FunctionContext<JsonElement> context, CancellationToken cancellationToken)
                {
                    var page = {|#0:LoadAsync().Result|};
                    {|#1:SaveAsync(page).ConfigureAwait(false).GetAwaiter().GetResult()|};
                    return page;
                }

                private static Task<string> LoadAsync() => Task.FromResult("x");

                private static Task SaveAsync(string page) => Task.CompletedTask;
            }
            """;
        const string fixedSource = """
            [InngestFunction("order")]
            public sealed class OnOrder : InngestFunction<JsonElement>
            {
                public override async Task<object?> ExecuteAsync(FunctionContext<JsonElement> context, CancellationToken cancellationToken)
                {
                    var page = await {|INNGEST028:LoadAsync|}();
                    await {|INNGEST028:SaveAsync|}(page).ConfigureAwait(false);
                    return page;
                }

                private static Task<string> LoadAsync() => Task.FromResult("x");

                private static Task SaveAsync(string page) => Task.CompletedTask;
            }
            """;

        var test = new CSharpCodeFixTest<FunctionBodyAnalyzer, AwaitStepCodeFixProvider, DefaultVerifier> { CodeActionEquivalenceKey = "Await the task" };
        ProjectSetup.Configure(test, properties: null);
        test.TestState.Sources.Add(Sources.Usings + source);
        test.TestState.ExpectedDiagnostics.Add(new DiagnosticResult(DiagnosticDescriptors.BlockingOnTask).WithLocation(0).WithArguments(".Result", "LoadAsync()"));
        test.TestState.ExpectedDiagnostics.Add(
            new DiagnosticResult(DiagnosticDescriptors.BlockingOnTask).WithLocation(1).WithArguments(".GetAwaiter().GetResult()", "SaveAsync()"));
        test.FixedState.Sources.Add(Sources.Usings + fixedSource);
        await test.RunAsync();
    }

    [Fact]
    public async Task Awaits_the_task_of_a_step_kept_in_a_local()
    {
        const string source = """
            [InngestFunction("order")]
            public sealed class OnOrder : InngestFunction<JsonElement>
            {
                public override async Task<object?> ExecuteAsync(FunctionContext<JsonElement> context, CancellationToken cancellationToken)
                {
                    var charging = context.Step.RunAsync("charge", () => 1);
                    return {|#0:charging.Result|};
                }
            }
            """;
        const string fixedSource = """
            [InngestFunction("order")]
            public sealed class OnOrder : InngestFunction<JsonElement>
            {
                public override async Task<object?> ExecuteAsync(FunctionContext<JsonElement> context, CancellationToken cancellationToken)
                {
                    var charging = context.Step.RunAsync("charge", () => 1);
                    return await charging;
                }
            }
            """;

        var test = new CSharpCodeFixTest<FunctionBodyAnalyzer, AwaitStepCodeFixProvider, DefaultVerifier> { CodeActionEquivalenceKey = "Await the step" };
        ProjectSetup.Configure(test, properties: null);
        test.TestState.Sources.Add(Sources.Usings + source);
        test.TestState.ExpectedDiagnostics.Add(new DiagnosticResult(DiagnosticDescriptors.BlockingOnStep).WithLocation(0).WithArguments(".Result", "RunAsync(\"charge\")"));
        test.FixedState.Sources.Add(Sources.Usings + fixedSource);
        await test.RunAsync();
    }

    // Both kinds share an id, so the analyzer tells the fix which one it reported.
    [Fact]
    public async Task The_analyzer_tells_the_fix_whether_the_task_is_a_step()
    {
        const string source = """
            [InngestFunction("order")]
            public sealed class OnOrder : InngestFunction<JsonElement>
            {
                public override async Task<object?> ExecuteAsync(FunctionContext<JsonElement> context, CancellationToken cancellationToken)
                {
                    var charging = context.Step.RunAsync("charge", () => 1);
                    return charging.Result + LoadAsync().Result;
                }

                private static Task<int> LoadAsync() => Task.FromResult(1);
            }
            """;
        var compilation = Generation.Compile("App", Sources.Usings + source);

        var diagnostics = await compilation.WithAnalyzers([new FunctionBodyAnalyzer()]).GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);

        var kinds = diagnostics
            .Where(diagnostic => diagnostic.Id == DiagnosticIds.BlockingOnStep)
            .OrderBy(diagnostic => diagnostic.Location.SourceSpan.Start)
            .Select(diagnostic => diagnostic.Properties.TryGetValue("Kind", out var kind) ? kind : null);
        kinds.ShouldBe([null, "Task"]);
    }

    private static async Task Run(string source, string fixedSource)
    {
        var test = new CSharpCodeFixTest<StepCallAnalyzer, AwaitStepCodeFixProvider, DefaultVerifier>();
        ProjectSetup.Configure(test, properties: null);
        test.TestState.Sources.Add(Sources.Usings + source);
        test.FixedState.Sources.Add(Sources.Usings + fixedSource);
        await test.RunAsync();
    }
}
