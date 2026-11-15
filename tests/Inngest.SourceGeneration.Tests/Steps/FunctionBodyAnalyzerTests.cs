using Inngest.SourceGeneration.Steps;
using Inngest.SourceGeneration.Tests.Support;
using Microsoft.CodeAnalysis.Testing;

namespace Inngest.SourceGeneration.Tests.Steps;

public sealed class FunctionBodyAnalyzerTests
{
    // INNGEST026

    [Fact]
    public async Task The_clock_a_new_guid_and_random_numbers_outside_a_step_are_reported()
    {
        const string body = """
            var now = {|#0:DateTime.UtcNow|};
            var local = {|#1:DateTimeOffset.Now|};
            var id = {|#2:Guid.NewGuid()|};
            var roll = {|#3:Random.Shared.Next(1, 7)|};
            var other = {|#4:new Random().NextDouble()|};
            var ticks = {|#5:Environment.TickCount64|};
            var at = {|#6:TimeProvider.System.GetUtcNow()|};
            var watch = {|#7:System.Diagnostics.Stopwatch.StartNew()|};
            var token = {|#8:System.Security.Cryptography.RandomNumberGenerator.GetInt32(100)|};
            return $"{now}{local}{id}{roll}{other}{ticks}{at}{watch}{token}";
            """;

        await Verify(
            Function(body),
            Value(0, "DateTime.UtcNow"),
            Value(1, "DateTimeOffset.Now"),
            Value(2, "Guid.NewGuid()"),
            Value(3, "Random.Shared.Next()"),
            Value(4, "new Random().NextDouble()"),
            Value(5, "Environment.TickCount64"),
            Value(6, "TimeProvider.GetUtcNow()"),
            Value(7, "Stopwatch.StartNew()"),
            Value(8, "RandomNumberGenerator.GetInt32()"));
    }

    [Fact]
    public async Task Values_inside_a_step_seeded_random_and_arguments_of_steps_logs_and_throws_are_fine()
    {
        const string body = """
            var now = await context.Step.RunAsync("now", () => DateTime.UtcNow);
            var id = await context.Step.RunAsync("id", () => Guid.NewGuid());
            var seeded = new Random(42).Next();
            await context.Step.SleepUntilAsync("later", DateTimeOffset.UtcNow.AddHours(1));
            context.Logger.LogInformation("Started at {At}", DateTime.UtcNow);
            if (context.Attempt == 0)
            {
                throw new RetryAfterException("not yet", DateTimeOffset.UtcNow.AddMinutes(5));
            }

            Func<DateTime> later = () => DateTime.UtcNow;
            DateTime Local() => DateTime.UtcNow;
            return $"{now}{id}{seeded}{later}{Local()}";
            """;

        await Verify(Function(body));
    }

    [Fact]
    public async Task A_delegate_function_and_a_helper_that_takes_the_context_are_bodies_too()
    {
        const string source = """
            public static class Setup
            {
                public static Func<FunctionContext<JsonElement>, CancellationToken, Task<object?>> Handler { get; } = async (context, cancellationToken) =>
                {
                    await context.Step.ParallelAsync(ParallelMode.Wait, async () => await Task.WhenAll(context.Step.RunAsync("a", () => 1), Task.FromResult({|#0:Guid.NewGuid()|}.ToString())));
                    return {|#1:DateTime.Now|};
                };

                public static Task<string> Helper(FunctionContext<JsonElement> context) => Task.FromResult({|#2:Guid.NewGuid()|}.ToString());

                // Middleware runs in every call by design, and takes the non-generic context.
                public static DateTime Middleware(FunctionContext context) => DateTime.UtcNow;

                public static DateTime Unrelated(IStepTools step) => DateTime.UtcNow;
            }
            """;

        await Verify(source, Value(0, "Guid.NewGuid()"), Value(1, "DateTime.Now"), Value(2, "Guid.NewGuid()"));
    }

    // INNGEST027

    [Fact]
    public async Task Task_Delay_and_Thread_Sleep_outside_a_step_are_reported()
    {
        const string body = """
            await Task.{|#0:Delay|}(1000);
            Thread.{|#1:Sleep|}(TimeSpan.FromSeconds(1));
            await Task.WhenAny(context.Step.RunAsync("work", () => 1), Task.{|#2:Delay|}(5000, cancellationToken));
            return null;
            """;

        await Verify(Function(body), Delay(0, "Task.Delay"), Delay(1, "Thread.Sleep"), Delay(2, "Task.Delay"));
    }

    [Fact]
    public async Task A_delay_inside_a_step_is_fine()
    {
        const string body = """
            await context.Step.RunAsync("backoff", async () => await Task.Delay(100));
            await context.Step.RunAsync("block", () => Thread.Sleep(10));
            await context.Step.SleepAsync("pause", TimeSpan.FromSeconds(5));
            return null;
            """;

        await Verify(Function(body));
    }

    // INNGEST028

    [Fact]
    public async Task An_awaited_call_that_is_not_a_step_is_reported()
    {
        const string source = """
            public interface IMailer
            {
                Task SendAsync(string to);
            }

            [InngestFunction("welcome")]
            public sealed class Welcome(IMailer mailer, HttpClient http, IInngestClient inngest) : InngestFunction<JsonElement>
            {
                public override async Task<object?> ExecuteAsync(FunctionContext<JsonElement> context, CancellationToken cancellationToken)
                {
                    await mailer.{|#0:SendAsync|}("a@example.com");
                    var page = await http.{|#1:GetStringAsync|}("https://example.com").ConfigureAwait(false);
                    await inngest.{|#2:SendAsync|}(new InngestEvent("user/welcomed"));
                    await Task.WhenAll(mailer.{|#3:SendAsync|}("b@example.com"), context.Step.RunAsync("c", () => 1));
                    await Task.WhenAny(new[] { mailer.{|#4:SendAsync|}("c@example.com"), context.Step.SleepAsync("d", TimeSpan.FromSeconds(1)) });
                    return page;
                }
            }
            """;

        await Verify(source, Awaited(0, "IMailer.SendAsync"), Awaited(1, "HttpClient.GetStringAsync"), Awaited(2, "IInngestClient.SendAsync"), Awaited(3, "IMailer.SendAsync"), Awaited(4, "IMailer.SendAsync"));
    }

    [Fact]
    public async Task Steps_calls_that_can_run_steps_and_coordination_are_fine_to_await()
    {
        const string source = """
            public static class Orders
            {
                public static Task Process(FunctionContext<JsonElement> context) => Task.CompletedTask;

                public static Task Charge(IStepTools step, string order) => step.RunAsync("charge", () => order);
            }

            [InngestFunction("orders")]
            public sealed class OnOrder(HttpClient http) : InngestFunction<JsonElement>
            {
                private readonly SemaphoreSlim _gate = new(1);

                public override async Task<object?> ExecuteAsync(FunctionContext<JsonElement> context, CancellationToken cancellationToken)
                {
                    var page = await context.Step.RunAsync("fetch", () => http.GetStringAsync("https://example.com"));
                    await Orders.Process(context);
                    await Orders.Charge(context.Step, page);
                    await _gate.WaitAsync(cancellationToken);
                    await Task.Yield();
                    using var response = await context.Step.FetchAsync("page", new HttpRequestMessage(HttpMethod.Get, "https://example.com"));
                    var text = await response.Content.ReadAsStringAsync(cancellationToken);
                    await Task.WhenAll(new[] { 1, 2 }.Select(item => context.Step.RunAsync($"item-{item}", () => item)));
                    async Task<int> Local() => await http.GetStreamAsync("https://example.com").ContinueWith(_ => 1);
                    await Local();
                    Func<Task> later = () => http.GetStringAsync("https://example.com");
                    await later();
                    var pending = http.GetStringAsync("https://example.com");
                    await pending;
                    return page;
                }
            }
            """;

        await Verify(source);
    }

    // INNGEST023

    [Fact]
    public async Task Blocking_on_a_task_the_function_starts_is_reported()
    {
        const string source = """
            [InngestFunction("order")]
            public sealed class OnOrder(HttpClient http) : InngestFunction<JsonElement>
            {
                public override async Task<object?> ExecuteAsync(FunctionContext<JsonElement> context, CancellationToken cancellationToken)
                {
                    var page = {|#0:http.GetStringAsync("https://example.com").Result|};
                    {|#1:SaveAsync(page).Wait()|};
                    var loading = LoadAsync();
                    var loaded = {|#2:loading.GetAwaiter().GetResult()|};
                    var again = loading.Result;
                    var configured = {|#3:LoadAsync().ConfigureAwait(false).GetAwaiter().GetResult()|};
                    var count = {|#4:CountAsync().Result|};
                    var sizes = new[] { "a" }.Select(item => {|#5:LoadAsync().Result|}.Length).Sum();
                    await context.Step.ParallelAsync(ParallelMode.Wait, async () => {|#6:SaveAsync(again).Wait()|});
                    var charging = context.Step.RunAsync("charge", () => 1);
                    var charged = {|#7:charging.Result|};
                    {|#8:Task.WhenAll(Task.Run(() => 1), LoadAsync()).Wait()|};

                    // A wait on the step call itself is StepCallAnalyzer's.
                    var direct = context.Step.RunAsync("direct", () => 2).Result;
                    return page + loaded + configured + count + sizes + charged + direct;
                }

                private static Task<string> LoadAsync() => Task.FromResult("x");

                private static Task SaveAsync(string page) => Task.CompletedTask;

                private static ValueTask<int> CountAsync() => new(1);
            }
            """;

        await Verify(
            source,
            Blocked(0, ".Result", "GetStringAsync()"),
            Blocked(1, ".Wait()", "SaveAsync()"),
            Blocked(2, ".GetAwaiter().GetResult()", "LoadAsync()"),
            Blocked(3, ".GetAwaiter().GetResult()", "LoadAsync()"),
            Blocked(4, ".Result", "CountAsync()"),
            Blocked(5, ".Result", "LoadAsync()"),
            Blocked(6, ".Wait()", "SaveAsync()"),
            new DiagnosticResult(DiagnosticDescriptors.BlockingOnStep).WithLocation(7).WithArguments(".Result", "RunAsync(\"charge\")"),
            Blocked(8, ".Wait()", "LoadAsync()"));
    }

    [Fact]
    public async Task Blocking_on_a_task_that_is_done_or_runs_elsewhere_is_fine()
    {
        const string source = """
            [InngestFunction("order")]
            public sealed class OnOrder : InngestFunction<JsonElement>
            {
                private static readonly Task<string> Cached = LoadAsync();

                public override async Task<object?> ExecuteAsync(FunctionContext<JsonElement> context, CancellationToken cancellationToken)
                {
                    var a = LoadAsync();
                    var b = LoadAsync();
                    await Task.WhenAll(a, b);
                    var both = a.Result + b.GetAwaiter().GetResult();

                    var c = LoadAsync();
                    var d = LoadAsync();
                    var first = await Task.WhenAny(c, d);
                    var winner = first.Result;

                    var e = LoadAsync();
                    var early = e.IsCompleted ? e.Result : "";

                    var offloaded = Task.Run(() => LoadAsync()).Result;
                    var inside = await Task.Run(() => LoadAsync().Result);
                    var done = Task.FromResult(1).Result + ValueTask.FromResult(2).Result;
                    Task.CompletedTask.Wait();
                    Task.WhenAll(Task.Run(() => 1), Task.Run(() => 2)).Wait();
                    var all = new List<Task<string>> { LoadAsync() };
                    Task.WhenAll(all).Wait();
                    var continued = await Task.Run(() => "x").ContinueWith(task => LoadAsync().Result + task.Result);
                    var step = await context.Step.RunAsync("step", () => LoadAsync().Result);
                    Func<string> later = () => LoadAsync().Result;
                    string Local() => LoadAsync().Result;
                    return both + winner + early + offloaded + inside + done + continued + step + later() + Local() + Passed(context, a);
                }

                private static string Passed(FunctionContext<JsonElement> context, Task<string> pending) => pending.Result + Cached.Result;

                private static Task<string> LoadAsync() => Task.FromResult("x");
            }
            """;

        await Verify(source);
    }

    [Fact]
    public async Task Blocking_through_WaitAsync_or_ContinueWith_is_reported_on_the_task_they_wait_for()
    {
        const string source = """
            [InngestFunction("order")]
            public sealed class OnOrder : InngestFunction<JsonElement>
            {
                public override async Task<object?> ExecuteAsync(FunctionContext<JsonElement> context, CancellationToken cancellationToken)
                {
                    var page = {|#0:LoadAsync().WaitAsync(TimeSpan.FromSeconds(5)).Result|};
                    var shouted = {|#1:LoadAsync().ContinueWith(task => task.Result + "!").Result|};
                    var loading = LoadAsync();
                    var loaded = {|#2:loading.WaitAsync(cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult()|};
                    var charged = {|#3:context.Step.RunAsync("charge", () => 1).WaitAsync(cancellationToken).Result|};
                    var doubled = {|#4:context.Step.RunAsync("double", () => 2).ContinueWith(task => task.Result * 2).Result|};
                    return page + shouted + loaded + charged + doubled;
                }

                private static Task<string> LoadAsync() => Task.FromResult("x");
            }
            """;

        await Verify(
            source,
            Blocked(0, ".Result", "LoadAsync()"),
            Blocked(1, ".Result", "LoadAsync()"),
            Blocked(2, ".GetAwaiter().GetResult()", "LoadAsync()"),
            new DiagnosticResult(DiagnosticDescriptors.BlockingOnStep).WithLocation(3).WithArguments(".Result", "RunAsync(\"charge\")"),
            new DiagnosticResult(DiagnosticDescriptors.BlockingOnStep).WithLocation(4).WithArguments(".Result", "RunAsync(\"double\")"));
    }

    [Fact]
    public async Task Blocking_through_WaitAsync_or_ContinueWith_on_a_task_that_runs_elsewhere_or_is_done_is_fine()
    {
        const string source = """
            [InngestFunction("order")]
            public sealed class OnOrder : InngestFunction<JsonElement>
            {
                public override async Task<object?> ExecuteAsync(FunctionContext<JsonElement> context, CancellationToken cancellationToken)
                {
                    var continued = Task.Run(() => 1).ContinueWith(task => task.Result + 1).Result;
                    var pooled = Task.Run(() => 2).ContinueWith(task => task.Result + 1, TaskScheduler.Default).Result;
                    var current = Task.Run(() => 3).ContinueWith(task => task.Result + 1, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Current).Result;
                    var bounded = Task.Run(() => "x").WaitAsync(TimeSpan.FromSeconds(5)).Result;
                    var done = Task.FromResult("y").WaitAsync(cancellationToken).Result;
                    return continued + pooled + current + bounded + done;
                }
            }
            """;

        await Verify(source);
    }

    [Fact]
    public async Task Blocking_on_a_task_after_a_WaitAsync_that_may_have_given_up_on_it_is_reported()
    {
        const string source = """
            [InngestFunction("order")]
            public sealed class OnOrder : InngestFunction<JsonElement>
            {
                public override async Task<object?> ExecuteAsync(FunctionContext<JsonElement> context, CancellationToken cancellationToken)
                {
                    var loading = LoadAsync();
                    try
                    {
                        await loading.WaitAsync(TimeSpan.FromSeconds(1));
                    }
                    catch (TimeoutException)
                    {
                    }

                    return {|#0:loading.Result|};
                }

                private static Task<string> LoadAsync() => Task.FromResult("x");
            }
            """;

        await Verify(source, Blocked(0, ".Result", "LoadAsync()"));
    }

    [Fact]
    public async Task Blocking_on_a_continuation_given_a_scheduler_of_its_own_is_reported()
    {
        const string source = """
            [InngestFunction("order")]
            public sealed class OnOrder(TaskScheduler scheduler) : InngestFunction<JsonElement>
            {
                public override async Task<object?> ExecuteAsync(FunctionContext<JsonElement> context, CancellationToken cancellationToken)
                {
                    var posted = {|#0:Task.Run(() => 1).ContinueWith(task => task.Result + 1, TaskScheduler.FromCurrentSynchronizationContext()).Result|};
                    var scheduled = Task.Run(() => 2).ContinueWith(task => task.Result * 2, CancellationToken.None, TaskContinuationOptions.None, scheduler);
                    {|#1:scheduled.Wait()|};
                    return posted + scheduled.Result;
                }
            }
            """;

        await Verify(source, Blocked(0, ".Result", "ContinueWith()"), Blocked(1, ".Wait()", "ContinueWith()"));
    }

    // INNGEST029

    [Fact]
    public async Task A_loop_over_an_unordered_collection_with_a_step_id_that_ignores_the_item_is_reported()
    {
        const string body = """
            var prices = new Dictionary<string, int> { ["a"] = 1 };
            var tags = new HashSet<string> { "x" };
            foreach (var price in {|#0:prices|})
            {
                await context.Step.RunAsync("charge", () => price.Value);
            }

            var i = 0;
            foreach (var tag in {|#1:tags|})
            {
                await context.Step.RunAsync($"tag-{i++}", () => tag);
            }

            foreach (var key in {|#2:prices.Keys|})
            {
                await context.Step.RunAsync("key", () => key);
                await context.Step.RunAsync("key-again", () => key);
            }

            return null;
            """;

        await Verify(
            Function(body),
            Unordered(0, "Dictionary", "RunAsync(\"charge\")"),
            Unordered(1, "HashSet", "RunAsync"),
            Unordered(2, "Dictionary", "RunAsync(\"key\")"));
    }

    [Fact]
    public async Task A_loop_whose_step_ids_come_from_the_item_or_over_an_ordered_collection_is_fine()
    {
        const string body = """
            var prices = new Dictionary<string, int> { ["a"] = 1 };
            foreach (var (name, amount) in prices)
            {
                await context.Step.RunAsync($"charge-{name}", () => amount);
            }

            foreach (var price in prices)
            {
                var id = "charge-" + price.Key;
                await context.Step.RunAsync(id, () => price.Value);
            }

            foreach (var price in prices.OrderBy(pair => pair.Key))
            {
                await context.Step.RunAsync("ordered", () => price.Value);
            }

            foreach (var item in new List<int> { 1, 2 })
            {
                await context.Step.RunAsync("listed", () => item);
            }

            return null;
            """;

        await Verify(Function(body));
    }

    // INNGEST032

    [Fact]
    public async Task Two_steps_with_the_same_literal_id_are_reported()
    {
        const string body = """
            await context.Step.RunAsync("charge", () => 1);
            await context.Step.SleepAsync({|#0:"charge"|}, TimeSpan.FromSeconds(1));
            await context.Step.ParallelAsync(ParallelMode.Wait, () => Task.WhenAll(
                context.Step.RunAsync("left", () => 1),
                context.Step.RunAsync({|#1:"left"|}, () => 2)));
            await context.Step.RunAsync({|#2:new StepOptions("charge") { Name = "Charge again" }|}, () => 3);
            return null;
            """;

        await Verify(
            Function(body),
            Repeated(0, "charge", "RunAsync", 17),
            Repeated(1, "left", "RunAsync", 20),
            Repeated(2, "charge", "RunAsync", 17));
    }

    [Fact]
    public async Task Ids_in_a_loop_or_in_other_branches_are_fine()
    {
        const string body = """
            foreach (var item in new[] { 1, 2 })
            {
                await context.Step.RunAsync("each", () => item);
            }

            if (context.Attempt > 0)
            {
                await context.Step.RunAsync("notify", () => 1);
            }
            else if (context.Attempt > 5)
            {
                await context.Step.RunAsync("notify", () => 2);
            }
            else
            {
                await context.Step.RunAsync("notify", () => 3);
            }

            switch (context.Attempt)
            {
                case 0:
                    await context.Step.RunAsync("route", () => 1);
                    break;
                default:
                    await context.Step.RunAsync("route", () => 2);
                    break;
            }

            var result = context.Attempt == 0
                ? await context.Step.RunAsync("pick", () => 1)
                : await context.Step.RunAsync("pick", () => 2);
            return result;
            """;

        await Verify(Function(body));
    }

    [Fact]
    public async Task Code_outside_a_function_body_is_left_alone()
    {
        const string source = """
            public static class Plain
            {
                public static async Task<string> Run(IStepTools step, HttpClient http)
                {
                    await Task.Delay(10);
                    await step.RunAsync("same", () => 1);
                    await step.RunAsync("same", () => 2);
                    return await http.GetStringAsync("https://example.com") + DateTime.UtcNow;
                }
            }
            """;

        await Verify(source);
    }

    internal static string Function(string body) => $$"""
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

    private static DiagnosticResult Value(int markup, string value) =>
        new DiagnosticResult(DiagnosticDescriptors.NondeterministicValue).WithLocation(markup).WithArguments(value);

    private static DiagnosticResult Delay(int markup, string call) =>
        new DiagnosticResult(DiagnosticDescriptors.DelayInFunction).WithLocation(markup).WithArguments(call);

    private static DiagnosticResult Awaited(int markup, string call) =>
        new DiagnosticResult(DiagnosticDescriptors.AwaitOutsideStep).WithLocation(markup).WithArguments(call);

    private static DiagnosticResult Blocked(int markup, string how, string call) =>
        new DiagnosticResult(DiagnosticDescriptors.BlockingOnTask).WithLocation(markup).WithArguments(how, call);

    private static DiagnosticResult Unordered(int markup, string collection, string step) =>
        new DiagnosticResult(DiagnosticDescriptors.UnorderedLoopOverSteps).WithLocation(markup).WithArguments(collection, step);

    private static DiagnosticResult Repeated(int markup, string id, string first, int line) =>
        new DiagnosticResult(DiagnosticDescriptors.RepeatedStepId).WithLocation(markup).WithArguments(id, first, line.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static async Task Verify(string source, params DiagnosticResult[] expected)
    {
        var test = new InngestAnalyzerTest<FunctionBodyAnalyzer>(Sources.Usings + "using System.Net.Http;\nusing Microsoft.Extensions.Logging;\n\n" + source);
        test.ExpectedDiagnostics.AddRange(expected);
        await test.RunAsync();
    }
}
