using System.Globalization;
using System.Text;
using System.Text.Json;
using Inngest.Execution;
using Inngest.Registration;
using Inngest.Soak.Workload;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Soak;

// Every invocation but the last of a run leaves the function waiting on a step that never completes in it. Nothing
// may keep such an invocation reachable once its call has been answered. Each variant runs a function that records
// weak references to what an abandoned invocation holds (its context, a buffer its code keeps across the step, the
// pending step's task), runs the calls, forces full collections and counts what is still alive.
//
// The last engine variant is a control: its functions register a callback on a cancellation token that outlives
// them, which keeps their buffers reachable on purpose. The check has to find every one of those alive, or it
// proves nothing.
internal static class AbandonCheck
{
    private const string Header = "variant,path,invocations,tracked_objects,alive_right_after_last_call,alive_after_500ms,expected_alive,status_ok";

    // Code that continues on the thread pool can still be finishing when the engine has answered, so the count that
    // is checked is taken a moment after the last call.
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(500);

    public static async Task<int> RunAsync(CommandLine options, CancellationToken cancellationToken)
    {
        var invocations = options.GetInt("invocations", 5000);
        var output = options.Get("out", "docs/results/soak");
        Directory.CreateDirectory(output);

        Row[] rows =
        [
            await EngineAsync("sleep-after-memoized-step", Variants.Sleep, Variants.LoadMemoized, invocations, cancellationToken),
            await EngineAsync("parallel-steps-reported", Variants.Parallel, Variants.NoMemo, invocations, cancellationToken),
            await EngineAsync("step-executed-and-awaited", Variants.Executed, Variants.NoMemo, invocations, cancellationToken),
            await EngineAsync("wait-registered-off-scheduler", Variants.OffScheduler, Variants.NoMemo, invocations, cancellationToken),
            await EngineAsync("inside-middleware-wrap", Variants.Sleep, Variants.LoadMemoized, invocations, cancellationToken, withMiddleware: true),
            await EngineAsync("token-registration-per-call-token", Variants.TokenRegistration, Variants.LoadMemoized, invocations, cancellationToken),
            await EngineAsync("control-token-registration-shared-token", Variants.TokenRegistration, Variants.LoadMemoized, invocations, cancellationToken, sharedToken: true),
            .. await HttpAsync(invocations, cancellationToken),
        ];

        var lines = new List<string> { Header };
        foreach (var row in rows)
        {
            var expected = row.ExpectedAlive.ToString(CultureInfo.InvariantCulture);
            lines.Add(string.Join(',', row.Variant, row.Path, row.Invocations, row.Tracked, row.AliveRightAfter, row.Alive, expected, row.StatusOk ? "true" : "false"));
            Console.WriteLine($"{row.Variant,-42} {row.Path,-24} {row.Invocations,6} invocations  {row.Tracked,6} tracked  {row.AliveRightAfter,6} alive at once, {row.Alive,6} after {Settle.TotalMilliseconds:0} ms (expected {expected})");
        }

        await File.WriteAllLinesAsync(Path.Combine(output, "abandoned.csv"), lines, cancellationToken);
        return rows.All(static row => row.StatusOk && row.Alive == row.ExpectedAlive) ? 0 : 1;
    }

    private static async Task<Row> EngineAsync(
        string variant,
        Func<Tracker, Func<FunctionContext<JsonElement>, CancellationToken, Task<object?>>> function,
        Func<int, string> body,
        int invocations,
        CancellationToken cancellationToken,
        bool withMiddleware = false,
        bool sharedToken = false)
    {
        var tracker = new Tracker(invocations * 4);
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddSingleton(tracker);
        var builder = services
            .AddInngest(static options =>
            {
                options.AppId = SoakServices.AppId;
                options.IsDev = true;
            })
            .AddFunction(new FunctionOptions { Id = "abandon", Triggers = [Trigger.Event("soak/abandon")] }, function(tracker));
        if (withMiddleware)
        {
            builder.AddMiddleware<TrackingMiddleware>();
        }

        var statusOk = true;
        using var shared = new CancellationTokenSource();
        await using (var provider = services.BuildServiceProvider())
        {
            var engine = provider.GetRequiredService<IExecutionEngine>();
            provider.GetRequiredService<IFunctionRegistry>().TryGet($"{SoakServices.AppId}-abandon", out var definition);
            var scopes = provider.GetRequiredService<IServiceScopeFactory>();
            await Parallel.ForEachAsync(
                Enumerable.Range(0, invocations),
                new ParallelOptions { MaxDegreeOfParallelism = 32, CancellationToken = cancellationToken },
                async (index, token) =>
                {
                    // A token per call, disposed with it, is what Kestrel hands out and what the Connect worker
                    // creates per execution.
                    using var call = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var scope = scopes.CreateAsyncScope();
                    await using (scope.ConfigureAwait(false))
                    {
                        var request = new ExecutionRequest { Function = definition!, Body = Encoding.UTF8.GetBytes(body(index)), StepId = "step", Services = scope.ServiceProvider };
                        using var result = await engine.ExecuteAsync(request, sharedToken ? shared.Token : call.Token).ConfigureAwait(false);
                        if (result.StatusCode != 206)
                        {
                            statusOk = false;
                        }
                    }
                });

            var aliveRightAfter = tracker.CountAlive();
            await Task.Delay(Settle, cancellationToken);
            var alive = tracker.CountAlive();
            GC.KeepAlive(shared);
            return new Row(variant, "engine", invocations, tracker.Count, aliveRightAfter, alive, sharedToken ? invocations : 0, statusOk);
        }
    }

    // The same through Kestrel, counted while the client keeps its connections open and again after it closed them,
    // to see whether Kestrel holds on to the token of a connection's last request.
    private static async Task<Row[]> HttpAsync(int invocations, CancellationToken cancellationToken)
    {
        var tracker = new Tracker(invocations * 4);
        await using var app = await HttpSoak.StartAppAsync(
            cancellationToken,
            inngest => inngest.AddFunction(new FunctionOptions { Id = "abandon", Triggers = [Trigger.Event("soak/abandon")] }, Variants.TokenRegistration(tracker)));
        var url = new Uri($"{app.Urls.Single()}/api/inngest?fnId={SoakServices.AppId}-abandon&stepId=step");

        const int Connections = 32;
        var statusOk = true;
        var client = HttpSoak.CreateClient(Connections);
        await Parallel.ForEachAsync(
            Enumerable.Range(0, invocations),
            new ParallelOptions { MaxDegreeOfParallelism = Connections, CancellationToken = cancellationToken },
            async (index, token) =>
            {
                using var request = HttpSoak.SignedRequest(url, Encoding.UTF8.GetBytes(Variants.LoadMemoized(index)));
                using var response = await client.SendAsync(request, token).ConfigureAwait(false);
                if ((int)response.StatusCode != 206)
                {
                    statusOk = false;
                }
            });

        var openRightAfter = tracker.CountAlive();
        await Task.Delay(Settle, cancellationToken);
        var open = tracker.CountAlive();
        client.Dispose();
        await Task.Delay(Settle, cancellationToken);
        var closed = tracker.CountAlive();

        return
        [
            new Row("token-registration-connections-open", "http (kestrel, signed)", invocations, tracker.Count, openRightAfter, open, 0, statusOk),
            new Row("token-registration-connections-closed", "http (kestrel, signed)", invocations, tracker.Count, closed, closed, 0, statusOk),
        ];
    }

    private sealed record Row(string Variant, string Path, int Invocations, int Tracked, int AliveRightAfter, int Alive, int ExpectedAlive, bool StatusOk);

    // Records weak references and counts those whose target survived full collections.
    private sealed class Tracker(int capacity)
    {
        private readonly WeakReference[] _references = new WeakReference[capacity];
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Track(object target) => _references[Interlocked.Increment(ref _count) - 1] = new WeakReference(target);

        public int CountAlive()
        {
            for (var i = 0; i < 3; i++)
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
            }

            return _references.Take(Count).Count(static reference => reference.IsAlive);
        }
    }

    private sealed class TrackingMiddleware(Tracker tracker) : InngestMiddleware
    {
        public override async Task<object?> WrapStepAsync(FunctionContext context, StepInfo step, Func<Task<object?>> next)
        {
            tracker.Track(this);
            return await next().ConfigureAwait(false);
        }
    }

    private static class Variants
    {
        private const string Event = """{"data":{"n":1},"id":"01JABANDON","name":"soak/abandon","ts":1793268000000,"user":{}}""";

        public static string NoMemo(int index) => Body(index, string.Empty, string.Empty);

        // "load" is memoized, so the function goes on to the step after it.
        public static string LoadMemoized(int index)
        {
            var hash = StepIds.Hash("load");
            return Body(index, $"\"{hash}\"", $"\"{hash}\":{{\"data\":{index}}}");
        }

        public static Func<FunctionContext<JsonElement>, CancellationToken, Task<object?>> Sleep(Tracker tracker) => async (context, _) =>
        {
            var buffer = Track(tracker, context);
            var loaded = await context.Step.RunAsync("load", () => buffer.Length);
            var nap = context.Step.SleepAsync("nap", TimeSpan.FromHours(1));
            tracker.Track(nap);
            await nap;
            return loaded + buffer.Length;
        };

        public static Func<FunctionContext<JsonElement>, CancellationToken, Task<object?>> Parallel(Tracker tracker) => async (context, _) =>
        {
            var buffer = Track(tracker, context);
            Task<int>[] parts = [context.Step.RunAsync("a", () => 1), context.Step.RunAsync("b", () => 2), context.Step.RunAsync("c", () => 3)];
            tracker.Track(parts[0]);
            var results = await Task.WhenAll(parts);
            return results.Sum() + buffer.Length;
        };

        // The step runs in this invocation, but its result only reaches the function in the next one.
        public static Func<FunctionContext<JsonElement>, CancellationToken, Task<object?>> Executed(Tracker tracker) => async (context, _) =>
        {
            var buffer = Track(tracker, context);
            var work = context.Step.RunAsync("work", () => buffer.Length);
            tracker.Track(work);
            return await work;
        };

        public static Func<FunctionContext<JsonElement>, CancellationToken, Task<object?>> OffScheduler(Tracker tracker) => async (context, _) =>
        {
            var buffer = Track(tracker, context);
            await Task.Delay(1).ConfigureAwait(false);
            var waiting = context.Step.WaitForEventAsync<JsonElement>("wait", new WaitForEventOptions("soak/never", TimeSpan.FromHours(1)));
            tracker.Track(waiting);
            await waiting.ConfigureAwait(false);
            return buffer.Length;
        };

        // A callback on the call's token keeps the buffer reachable for as long as the token's source is.
        public static Func<FunctionContext<JsonElement>, CancellationToken, Task<object?>> TokenRegistration(Tracker tracker) => async (context, cancellationToken) =>
        {
            var buffer = Track(tracker, context);
            using var registration = cancellationToken.Register(static state => Array.Clear((byte[])state!), buffer);
            var loaded = await context.Step.RunAsync("load", () => buffer.Length);
            await context.Step.SleepAsync("nap", TimeSpan.FromHours(1));
            return loaded;
        };

        private static byte[] Track(Tracker tracker, FunctionContext context)
        {
            var buffer = new byte[256];
            tracker.Track(context);
            tracker.Track(buffer);
            return buffer;
        }

        private static string Body(int index, string stack, string steps) =>
            string.Create(
                CultureInfo.InvariantCulture,
                $$"""{"ctx":{"attempt":0,"disable_immediate_execution":false,"fn_id":"0c7e","max_attempts":4,"run_id":"01JABANDON{{index:D16}}","stack":{"current":{{(stack.Length == 0 ? 0 : 1)}},"stack":[{{stack}}]},"use_api":false},"event":{{Event}},"events":[{{Event}}],"steps":{{{steps}}},"use_api":false,"version":2}""");
    }
}
