using Inngest.SourceGeneration.Steps;
using Inngest.SourceGeneration.Tests.Support;
using Microsoft.CodeAnalysis.Testing;

namespace Inngest.SourceGeneration.Tests.Steps;

public sealed class StepCleanupAnalyzerTests
{
    [Fact]
    public async Task A_finally_block_or_a_using_that_encloses_a_step_is_pointed_out()
    {
        const string source = """
            public static class Cleanup
            {
                private static readonly SemaphoreSlim Gate = new(1, 1);

                public static async Task Locked(IStepTools step)
                {
                    await Gate.WaitAsync();
                    try
                    {
                        await step.RunAsync("charge", () => 1);
                    }
                    {|#0:finally|}
                    {
                        Gate.Release();
                    }
                }

                public static async Task Statement(IStepTools step)
                {
                    {|#1:using|} (var source = new CancellationTokenSource())
                    {
                        await step.SleepAsync("nap", TimeSpan.FromSeconds(1));
                    }
                }

                public static async Task Declaration(IStepTools step, IServiceProvider services)
                {
                    await {|#2:using|} var scope = services.CreateAsyncScope();
                    await step.ParallelAsync(ParallelMode.Wait, async () => await step.RunAsync("in-parallel", () => 2));
                }

                public static async Task InCatch(IStepTools step)
                {
                    try
                    {
                        throw new InvalidOperationException();
                    }
                    catch (InvalidOperationException)
                    {
                        await step.RunAsync("compensate", () => 3);
                    }
                    {|#3:finally|}
                    {
                        Console.WriteLine("done");
                    }
                }
            }
            """;

        await Verify(
            source,
            Encloses(0, "finally block", "RunAsync(\"charge\")"),
            Encloses(1, "using statement", "SleepAsync(\"nap\")"),
            Encloses(2, "using declaration", "RunAsync(\"in-parallel\")"),
            Encloses(3, "finally block", "RunAsync(\"compensate\")"));
    }

    [Fact]
    public async Task Cleanup_that_encloses_no_step_or_only_code_that_runs_elsewhere_is_fine()
    {
        const string source = """
            public static class Cleanup
            {
                public static async Task Caught(IStepTools step)
                {
                    try
                    {
                        await step.RunAsync("charge", () => 1);
                    }
                    catch (StepFailedException)
                    {
                    }
                }

                public static async Task AfterTheSteps(IStepTools step)
                {
                    await step.RunAsync("charge", () => 1);
                    using var source = new CancellationTokenSource();
                    try
                    {
                        source.Cancel();
                    }
                    finally
                    {
                        source.Dispose();
                    }
                }

                public static async Task InsideTheStep(IStepTools step)
                {
                    await step.RunAsync("download", async () =>
                    {
                        using var http = new HttpClient();
                        return await http.GetStringAsync("https://example.com");
                    });
                }

                public static Func<Task<int>> Later(IStepTools step)
                {
                    using var source = new CancellationTokenSource();
                    Func<Task<int>> later = () => step.RunAsync("later", () => 1);
                    return later;
                }
            }
            """;

        await Verify(source);
    }

    private static DiagnosticResult Encloses(int markup, string kind, string step) =>
        new DiagnosticResult(DiagnosticDescriptors.CleanupAroundStep).WithLocation(markup).WithArguments(kind, step);

    private static async Task Verify(string source, params DiagnosticResult[] expected)
    {
        var test = new InngestAnalyzerTest<StepCleanupAnalyzer>(
            Sources.Usings + "using System.Net.Http;\nusing Microsoft.Extensions.DependencyInjection;\n\n" + source);
        test.ExpectedDiagnostics.AddRange(expected);
        await test.RunAsync();
    }
}
