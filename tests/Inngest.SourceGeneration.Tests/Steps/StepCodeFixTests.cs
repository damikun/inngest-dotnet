using Inngest.SourceGeneration.Steps;
using Inngest.SourceGeneration.Tests.Support;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace Inngest.SourceGeneration.Tests.Steps;

public sealed class StepCodeFixTests
{
    // INNGEST026

    [Fact]
    public async Task Gets_the_value_in_a_step_with_an_id_the_function_does_not_use()
    {
        const string body = """
            await context.Step.RunAsync("utc-now", () => 0);
            var started = {|INNGEST026:DateTime.UtcNow|};
            var id = {|INNGEST026:Guid.NewGuid()|}.ToString("N");
            var roll = {|INNGEST026:Random.Shared.Next(1, 7)|} + 1;
            return $"{started}{id}{roll}";
            """;
        const string fixedBody = """
            await context.Step.RunAsync("utc-now", () => 0);
            var started = await context.Step.RunAsync("utc-now-2", () => DateTime.UtcNow);
            var id = (await context.Step.RunAsync("new-guid", () => Guid.NewGuid())).ToString("N");
            var roll = await context.Step.RunAsync("random-number", () => Random.Shared.Next(1, 7)) + 1;
            return $"{started}{id}{roll}";
            """;

        await Run<RunInStepCodeFixProvider>(Function(body), Function(fixedBody));
    }

    [Fact]
    public async Task Calls_the_steps_of_the_context_parameter_whatever_its_name()
    {
        const string source = """
            public static class Setup
            {
                public static Func<FunctionContext<JsonElement>, CancellationToken, Task<object?>> Handler { get; } = async (ctx, ct) =>
                {
                    await Task.Yield();
                    return {|INNGEST026:DateTimeOffset.UtcNow|};
                };
            }
            """;
        const string fixedSource = """
            public static class Setup
            {
                public static Func<FunctionContext<JsonElement>, CancellationToken, Task<object?>> Handler { get; } = async (ctx, ct) =>
                {
                    await Task.Yield();
                    return await ctx.Step.RunAsync("utc-now", () => DateTimeOffset.UtcNow);
                };
            }
            """;

        await Run<RunInStepCodeFixProvider>(source, fixedSource);
    }

    [Fact]
    public async Task Offers_no_step_where_the_code_cannot_await_or_the_value_is_an_object()
    {
        const string source = """
            [InngestFunction("order")]
            public sealed class OnOrder : InngestFunction<JsonElement>
            {
                public override Task<object?> ExecuteAsync(FunctionContext<JsonElement> context, CancellationToken cancellationToken)
                {
                    var now = {|INNGEST026:DateTime.UtcNow|};
                    return Task.FromResult<object?>(now);
                }

                public static async Task<object?> Watch(FunctionContext<JsonElement> context)
                {
                    await Task.Yield();
                    var watch = {|INNGEST026:System.Diagnostics.Stopwatch.StartNew()|};
                    var random = {|INNGEST026:new Random()|};
                    return $"{watch}{random}";
                }
            }
            """;

        await Run<RunInStepCodeFixProvider>(source, source);
    }

    // INNGEST027

    [Fact]
    public async Task Sleeps_in_a_step_instead_of_delaying()
    {
        const string body = """
            await Task.{|INNGEST027:Delay|}(5000);
            await Task.{|INNGEST027:Delay|}(TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
            Thread.{|INNGEST027:Sleep|}(250);
            return null;
            """;
        const string fixedBody = """
            await context.Step.SleepAsync("sleep", TimeSpan.FromSeconds(5));
            await context.Step.SleepAsync("sleep-2", TimeSpan.FromMinutes(1)).ConfigureAwait(false);
            await context.Step.SleepAsync("sleep-3", TimeSpan.FromMilliseconds(250));
            return null;
            """;

        await Run<SleepStepCodeFixProvider>(Function(body), Function(fixedBody));
    }

    [Fact]
    public async Task Offers_no_sleep_for_a_delay_that_is_not_awaited_where_it_is_made()
    {
        const string body = """
            await Task.WhenAny(context.Step.RunAsync("work", () => 1), Task.{|INNGEST027:Delay|}(1000));
            var delay = Task.{|INNGEST027:Delay|}(1000);
            await delay;
            return null;
            """;

        await Run<SleepStepCodeFixProvider>(Function(body), Function(body));
    }

    private static string Function(string body) => FunctionBodyAnalyzerTests.Function(body);

    private static async Task Run<TCodeFix>(string source, string fixedSource)
        where TCodeFix : CodeFixProvider, new() =>
        await Test<TCodeFix>(source, fixedSource).RunAsync();

    private static CSharpCodeFixTest<FunctionBodyAnalyzer, TCodeFix, DefaultVerifier> Test<TCodeFix>(string source, string fixedSource)
        where TCodeFix : CodeFixProvider, new()
    {
        var test = new CSharpCodeFixTest<FunctionBodyAnalyzer, TCodeFix, DefaultVerifier>();
        ProjectSetup.Configure(test, properties: null);
        test.TestState.Sources.Add(Sources.Usings + source);
        test.FixedState.Sources.Add(Sources.Usings + fixedSource);
        return test;
    }
}
