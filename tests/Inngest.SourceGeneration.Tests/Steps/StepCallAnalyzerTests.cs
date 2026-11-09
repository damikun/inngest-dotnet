using Inngest.SourceGeneration.Steps;
using Inngest.SourceGeneration.Tests.Support;
using Microsoft.CodeAnalysis.Testing;

namespace Inngest.SourceGeneration.Tests.Steps;

public sealed class StepCallAnalyzerTests
{
    // INNGEST020

    [Fact]
    public async Task A_local_that_a_handler_assigns_and_the_function_reads_later_is_reported()
    {
        const string body = """
            string? receipt = null;
            await context.Step.RunAsync("charge", () => { {|#0:receipt|} = "r-1"; });
            return receipt;
            """;

        await Verify(Function(body), new DiagnosticResult(DiagnosticDescriptors.StateAssignedInStep).WithLocation(0).WithArguments("receipt", "RunAsync(\"charge\")"));
    }

    [Fact]
    public async Task Increments_compound_assignments_and_out_arguments_count_as_assignments()
    {
        const string body = """
            var count = 0;
            var total = 0;
            var parsed = 0;
            await context.Step.RunAsync("count", () => {|#0:count|}++);
            await context.Step.RunAsync("total", () => { {|#1:total|} += 2; });
            await context.Step.RunAsync("parse", () => int.TryParse("4", out {|#2:parsed|}));
            return count + total + parsed;
            """;

        await Verify(Function(body), Assigned(0, "count", "RunAsync(\"count\")"), Assigned(1, "total", "RunAsync(\"total\")"), Assigned(2, "parsed", "RunAsync(\"parse\")"));
    }

    [Fact]
    public async Task A_field_of_the_function_that_a_handler_assigns_and_the_function_reads_is_reported()
    {
        const string source = """
            [InngestFunction("order")]
            public sealed class OnOrder : InngestFunction<JsonElement>
            {
                private int _attempts;
                private static string? s_last;

                public override async Task<object?> ExecuteAsync(FunctionContext<JsonElement> context, CancellationToken cancellationToken)
                {
                    await context.Step.RunAsync("one", () => { {|#0:_attempts|} = 1; });
                    await context.Step.RunAsync("two", () => { {|#1:s_last|} = "two"; });
                    return _attempts + s_last!.Length;
                }
            }
            """;

        await Verify(source, Assigned(0, "_attempts", "RunAsync(\"one\")"), Assigned(1, "s_last", "RunAsync(\"two\")"));
    }

    [Fact]
    public async Task The_handler_of_each_item_of_MapAsync_counts_as_a_step()
    {
        const string body = """
            var last = 0;
            await context.Step.MapAsync("items", new[] { 1, 2 }, (item, ct) => { {|#0:last|} = item; return Task.FromResult(item); });
            return last;
            """;

        await Verify(Function(body), Assigned(0, "last", "MapAsync(\"items\")"));
    }

    [Fact]
    public async Task What_a_step_returns_and_what_only_the_handler_reads_is_fine()
    {
        const string body = """
            var amount = 1;
            amount = await context.Step.RunAsync("add-one", () => amount + 1);
            var notes = new List<string>();
            await context.Step.RunAsync("note", () => notes.Add("changed through a reference, not assigned"));
            await context.Step.RunAsync("scratch", () =>
            {
                var local = 0;
                local++;
                var unreadOutside = 0;
                return local;
            });
            string? written = null;
            await context.Step.RunAsync("only-written", () => { written = "never read again"; });
            await context.Step.ParallelAsync(ParallelMode.Wait, async () => { amount = await context.Step.RunAsync("in-parallel", () => 2); });
            return amount + notes.Count;
            """;

        await Verify(Function(body));
    }

    [Fact]
    public async Task A_read_in_the_function_around_a_ParallelAsync_callback_counts()
    {
        const string body = """
            var fastest = "";
            await context.Step.ParallelAsync(ParallelMode.Race, async () =>
            {
                await context.Step.RunAsync("a", () => { {|#0:fastest|} = "a"; });
            });
            return fastest;
            """;

        await Verify(Function(body), Assigned(0, "fastest", "RunAsync(\"a\")"));
    }

    [Fact]
    public async Task A_read_after_the_function_has_returned_is_not_the_function_s_business()
    {
        const string source = """
            public static class Probe
            {
                public static (Func<FunctionContext<JsonElement>, Task<object?>> Function, Func<bool> Ran) Define()
                {
                    var ran = false;
                    Func<FunctionContext<JsonElement>, Task<object?>> function = async context =>
                    {
                        await context.Step.RunAsync("a", () => { ran = true; });
                        return null;
                    };

                    return (function, () => ran);
                }
            }
            """;

        await Verify(source);
    }

    // INNGEST022

    [Fact]
    public async Task An_id_built_from_the_clock_a_new_guid_or_a_random_number_is_reported()
    {
        const string body = """
            await context.Step.RunAsync({|#0:$"daily-{DateTime.UtcNow:yyyyMMdd}"|}, () => 1);
            await context.Step.SleepAsync({|#1:Guid.NewGuid().ToString()|}, TimeSpan.FromSeconds(1));
            var id = $"pick-{Random.Shared.Next()}";
            await context.Step.RunAsync({|#2:id|}, () => 2);
            await context.Step.RunAsync({|#3:new StepOptions($"opt-{DateTimeOffset.Now.ToUnixTimeSeconds()}")|}, () => 3);
            await context.Step.SendEventAsync({|#4:"send-" + Environment.TickCount64|}, new InngestEvent("shop/x", new { }));
            return null;
            """;

        await Verify(
            Function(body),
            Id(0, "RunAsync", "DateTime.UtcNow"),
            Id(1, "SleepAsync", "Guid.NewGuid()"),
            Id(2, "RunAsync", "Random.Next()"),
            Id(3, "RunAsync", "DateTimeOffset.Now"),
            Id(4, "SendEventAsync", "Environment.TickCount64"));
    }

    [Fact]
    public async Task An_id_from_the_event_a_step_result_or_a_seeded_random_is_fine()
    {
        const string body = """
            var token = await context.Step.RunAsync("token", () => Guid.NewGuid().ToString());
            await context.Step.SleepAsync($"wait-{token}", TimeSpan.FromSeconds(1));
            await context.Step.RunAsync($"order-{context.Event.Data.GetProperty("id").GetString()}", () => 1);
            await context.Step.RunAsync(new StepOptions("named") { Name = $"Named at {DateTime.UtcNow}" }, () => 2);
            await context.Step.RunAsync($"seeded-{new Random(42).Next()}", () => 3);
            await context.Step.RunAsync("inside", () => DateTime.UtcNow.Ticks);
            return null;
            """;

        await Verify(Function(body));
    }

    // INNGEST023

    [Fact]
    public async Task Blocking_on_the_task_of_a_step_is_reported()
    {
        const string source = """
            public static class Blocking
            {
                public static int Result(IStepTools step) => {|#0:step.RunAsync("a", () => 1).Result|};

                public static void Wait(IStepTools step) => {|#1:step.SleepAsync("b", TimeSpan.FromSeconds(1)).Wait()|};

                public static int Awaiter(IStepTools step) => {|#2:step.RunAsync("c", () => 3).GetAwaiter().GetResult()|};

                public static int Configured(IStepTools step) => {|#3:step.RunAsync("d", () => 4).ConfigureAwait(false).GetAwaiter().GetResult()|};
            }
            """;

        await Verify(
            source,
            Blocked(0, ".Result", "RunAsync(\"a\")"),
            Blocked(1, ".Wait()", "SleepAsync(\"b\")"),
            Blocked(2, ".GetAwaiter().GetResult()", "RunAsync(\"c\")"),
            Blocked(3, ".GetAwaiter().GetResult()", "RunAsync(\"d\")"));
    }

    [Fact]
    public async Task Reading_the_result_of_a_task_that_was_awaited_is_fine()
    {
        const string body = """
            var a = context.Step.RunAsync("a", () => 1);
            var b = context.Step.RunAsync("b", () => 2);
            await Task.WhenAll(a, b);
            return a.Result + b.Result + await context.Step.RunAsync("c", () => 3);
            """;

        await Verify(Function(body));
    }

    // INNGEST024

    [Fact]
    public async Task A_step_inside_the_handler_of_another_is_reported()
    {
        const string body = """
            await context.Step.RunAsync("outer", async () =>
            {
                await context.Step.{|#0:RunAsync|}("inner", () => 1);
                await context.Step.{|#1:SendEventAsync|}("send", new InngestEvent("shop/x", new { }));
            });
            await context.Step.ForEachAsync("items", new[] { 1 }, async (item, ct) => await context.Step.{|#2:SleepAsync|}("nap", TimeSpan.FromSeconds(item)));
            return null;
            """;

        await Verify(
            Function(body),
            Nested(0, "RunAsync(\"inner\")", "RunAsync(\"outer\")"),
            Nested(1, "SendEventAsync(\"send\")", "RunAsync(\"outer\")"),
            Nested(2, "SleepAsync(\"nap\")", "ForEachAsync(\"items\")"));
    }

    [Fact]
    public async Task Steps_one_after_the_other_in_parallel_or_in_a_continuation_are_fine()
    {
        const string body = """
            var a = await context.Step.RunAsync("a", () => "a");
            await context.Step.ParallelAsync(ParallelMode.Wait, async () =>
            {
                await Task.WhenAll(context.Step.RunAsync("b", () => a), context.Step.RunAsync("c", () => a));
            });
            await context.Step.RunAsync("d", () => "d").ContinueWith(d => context.Step.RunAsync("e", () => d.Result + "e")).Unwrap();
            return null;
            """;

        await Verify(Function(body));
    }

    // INNGEST025

    [Fact]
    public async Task A_send_or_a_step_whose_task_is_dropped_outside_an_async_function_is_reported()
    {
        const string source = """
            public sealed class Notifier(IInngestClient client)
            {
                public void Notify() => client.{|#0:SendAsync|}(new InngestEvent("shop/x", new { }));

                public void Schedule(IStepTools step)
                {
                    step.{|#1:SleepAsync|}("nap", TimeSpan.FromSeconds(1));
                }

                public Task Handler(IStepTools step) => step.RunAsync("send", () => { client.{|#2:SendAsync|}(new InngestEvent("shop/y", new { })); });
            }
            """;

        await Verify(
            source,
            Dropped(0, "SendAsync"),
            Dropped(1, "SleepAsync(\"nap\")"),
            Dropped(2, "SendAsync"));
    }

    [Fact]
    public async Task A_task_that_is_awaited_returned_or_discarded_on_purpose_is_fine()
    {
        const string source = """
            public sealed class Notifier(IInngestClient client)
            {
                public Task Returned() => client.SendAsync(new InngestEvent("shop/x", new { }));

                public void Discarded() => _ = client.SendAsync(new InngestEvent("shop/x", new { }));

                public async Task Awaited(IStepTools step)
                {
                    await client.SendAsync(new InngestEvent("shop/x", new { }));
                    await step.RunAsync("send", () => client.SendAsync(new InngestEvent("shop/y", new { })));
                    _ = step.SleepAsync("nap", TimeSpan.FromSeconds(1));
                }
            }
            """;

        await Verify(source);
    }

    [Fact]
    public async Task Code_without_steps_is_left_alone()
    {
        const string source = """
            public static class Plain
            {
                public static int Read(Task<int> task) => task.Result;

                public static string Id() => $"id-{Guid.NewGuid()}";

                public static void Fire(HttpClient http) => http.GetAsync("https://example.com");
            }
            """;

        await Verify(source);
    }

    private static string Function(string body) => $$"""
        [InngestFunction("order")]
        public sealed class OnOrder : InngestFunction<JsonElement>
        {
            public override async Task<object?> ExecuteAsync(FunctionContext<JsonElement> context, CancellationToken cancellationToken)
            {
        {{Indent(body, 8)}}
            }
        }
        """;

    private static string Indent(string text, int spaces) =>
        string.Join('\n', text.Split('\n').Select(line => line.Length == 0 ? line : new string(' ', spaces) + line));

    private static DiagnosticResult Assigned(int markup, string name, string step) =>
        new DiagnosticResult(DiagnosticDescriptors.StateAssignedInStep).WithLocation(markup).WithArguments(name, step);

    private static DiagnosticResult Id(int markup, string step, string source) =>
        new DiagnosticResult(DiagnosticDescriptors.NondeterministicStepId).WithLocation(markup).WithArguments(step, source);

    private static DiagnosticResult Blocked(int markup, string how, string step) =>
        new DiagnosticResult(DiagnosticDescriptors.BlockingOnStep).WithLocation(markup).WithArguments(how, step);

    private static DiagnosticResult Nested(int markup, string inner, string outer) =>
        new DiagnosticResult(DiagnosticDescriptors.NestedStep).WithLocation(markup).WithArguments(inner, outer);

    private static DiagnosticResult Dropped(int markup, string call) =>
        new DiagnosticResult(DiagnosticDescriptors.StepNotAwaited).WithLocation(markup).WithArguments(call);

    private static async Task Verify(string source, params DiagnosticResult[] expected)
    {
        var test = new InngestAnalyzerTest<StepCallAnalyzer>(Sources.Usings + "using System.Net.Http;\n\n" + source);
        test.ExpectedDiagnostics.AddRange(expected);
        await test.RunAsync();
    }
}
