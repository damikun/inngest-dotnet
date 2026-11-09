using Inngest.SourceGeneration.Steps;
using Inngest.SourceGeneration.Tests.Support;
using Microsoft.CodeAnalysis.CSharp.Testing;
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

    private static async Task Run(string source, string fixedSource)
    {
        var test = new CSharpCodeFixTest<StepCallAnalyzer, AwaitStepCodeFixProvider, DefaultVerifier>();
        ProjectSetup.Configure(test, properties: null);
        test.TestState.Sources.Add(Sources.Usings + source);
        test.FixedState.Sources.Add(Sources.Usings + fixedSource);
        await test.RunAsync();
    }
}
