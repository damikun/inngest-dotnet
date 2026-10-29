using System.Text.Json;
using Inngest.Functions;
using Inngest.Tests.Execution;

namespace Inngest.Tests.Stress;

// Thousands of invocations on one engine at once. Every invocation carries its own run number in its event, step ids
// and results, so an answer that belongs to another invocation, or state that leaks between them, shows up as a
// wrong body.
public sealed class EngineStressTests : IDisposable
{
    private const int Invocations = 4000;
    private const int Parts = 5;

    private readonly EngineHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Concurrent_invocations_with_parallel_steps_each_get_their_own_answer()
    {
        var function = TestFunction.Define(async context =>
        {
            var run = context.Event.Data.GetProperty("run").GetInt32();
            var parts = await Task.WhenAll(Enumerable.Range(0, Parts).Select(part => context.Step.RunAsync(Part(run, part), () => $"{run}:{part}")));
            return $"{run}=" + string.Join(',', parts);
        });

        var results = await RunAllAsync(function, ParallelCall);

        for (var run = 0; run < Invocations; run++)
        {
            var result = results[run];
            switch (run % 4)
            {
                case 0:
                    result.StatusCode.ShouldBe(206);
                    result.OpCodes.ShouldAllBe(op => op == "StepPlanned");
                    result.DisplayNames.ShouldBe([.. Enumerable.Range(0, Parts).Select(part => Part(run, part))]);
                    break;
                case 1:
                    result.OpCodes.ShouldAllBe(op => op == "StepPlanned");
                    result.DisplayNames.ShouldBe([Part(run, 0), Part(run, 2), Part(run, 4)]);
                    break;
                case 2:
                    result.StatusCode.ShouldBe(200);
                    result.Body.ShouldBe($"\"{run}={string.Join(',', Enumerable.Range(0, Parts).Select(part => $"{run}:{part}"))}\"");
                    break;
                default:
                    result.OpCodes.ShouldBe(["StepRun"]);
                    result.Op().GetProperty("displayName").GetString().ShouldBe(Part(run, 2));
                    result.Op().GetProperty("data").GetString().ShouldBe($"{run}:2");
                    break;
            }
        }
    }

    // Code that awaits with ConfigureAwait(false), yields or calls steps from Task.Run leaves the invocation's
    // scheduler. The engine may then see fewer of the parallel steps when it decides, but never wrong ones.
    [Fact]
    public async Task Code_that_leaves_the_scheduler_still_gets_its_own_answers()
    {
        var function = TestFunction.Define(async context =>
        {
            var run = context.Event.Data.GetProperty("run").GetInt32();
            var first = await context.Step.RunAsync($"first-{run}", () => $"{run}:first").ConfigureAwait(false);
            await Task.Yield();
            var parts = await Task.WhenAll(Enumerable.Range(0, 3).Select(part => Task.Run(() => context.Step.RunAsync(Part(run, part), () => $"{run}:{part}"))))
                .ConfigureAwait(false);
            await Task.Delay(1).ConfigureAwait(false);
            return $"{first}|{string.Join(',', parts)}";
        });

        var results = await RunAllAsync(function, OffSchedulerCall);

        for (var run = 0; run < Invocations; run++)
        {
            var result = results[run];
            switch (run % 4)
            {
                case 0:
                    result.OpCodes.ShouldBe(["StepRun"]);
                    result.Op().GetProperty("displayName").GetString().ShouldBe($"first-{run}");
                    result.Op().GetProperty("data").GetString().ShouldBe($"{run}:first");
                    break;
                case 1:
                    ShouldBeSomeOfTheParallelSteps(result, run);
                    break;
                case 2:
                    result.StatusCode.ShouldBe(200);
                    result.Body.ShouldBe($"\"{run}:first|{run}:0,{run}:1,{run}:2\"");
                    break;
                default:
                    result.OpCodes.ShouldBe(["StepRun"]);
                    result.Op().GetProperty("displayName").GetString().ShouldBe(Part(run, 2));
                    result.Op().GetProperty("data").GetString().ShouldBe($"{run}:2");
                    break;
            }
        }
    }

    // A function that sends an event from a step, thousands of times at once: each send goes through the client with
    // its own body, and each invocation gets back the ids of its own event.
    [Fact]
    public async Task Concurrent_event_sends_from_steps_get_their_own_ids()
    {
        var api = new EventApi();
        using var provider = StressServices.Build(api, inngest => inngest.AddFunction(
            new FunctionOptions { Id = "announce", Triggers = [Trigger.Event("test/event")] },
            async (context, _) =>
            {
                var run = context.Event.Data.GetProperty("run").GetInt32();
                var sent = await context.Step.SendEventAsync($"announce-{run}", new InngestEvent("test/announced", new { run }));
                return sent.Ids[0];
            }));
        var engine = StressServices.Engine(provider, "announce", out var definition);

        var results = await Task.WhenAll(Enumerable.Range(0, Invocations).Select(run => Task.Run(() =>
            StressServices.CallAsync(provider, engine, definition, Call.New().Event(Event(run)).ToBytes()))));

        for (var run = 0; run < Invocations; run++)
        {
            var result = results[run];
            result.OpCodes.ShouldBe(["StepRun"]);
            result.Op().GetProperty("data").GetProperty("ids").EnumerateArray().Select(static id => id.GetString()).ShouldBe([$"event-{run}"]);
        }

        api.Requests.ShouldBe(Invocations);
    }

    private static void ShouldBeSomeOfTheParallelSteps(CallResult result, int run)
    {
        result.StatusCode.ShouldBe(206);
        var expected = Enumerable.Range(0, 3).ToDictionary(part => Part(run, part), part => $"{run}:{part}");
        var names = result.DisplayNames;
        names.ShouldBeUnique();
        names.ShouldAllBe(name => expected.ContainsKey(name));
        if (names.Length == 1)
        {
            // The only step found yet runs at once.
            result.OpCodes.ShouldBe(["StepRun"]);
            result.Op().GetProperty("data").GetString().ShouldBe(expected[names[0]]);
        }
        else
        {
            result.OpCodes.ShouldAllBe(op => op == "StepPlanned");
        }
    }

    private async Task<CallResult[]> RunAllAsync(FunctionDefinition function, Func<int, (Call Call, string StepId)> shape) =>
        await Task.WhenAll(Enumerable.Range(0, Invocations).Select(run =>
        {
            var (call, stepId) = shape(run);
            return Task.Run(() => _host.CallAsync(function, call, stepId));
        }));

    // Four kinds of call, one after another: the first call, a parallel section half done, the last call, and a call
    // for one of the parallel steps.
    private static (Call Call, string StepId) ParallelCall(int run)
    {
        var call = Call.New().Event(Event(run));
        switch (run % 4)
        {
            case 0:
                return (call, "step");
            case 1:
                return (call.Data(Part(run, 3), $"\"{run}:3\"").Data(Part(run, 1), $"\"{run}:1\""), "step");
            case 2:
                foreach (var part in new[] { 4, 1, 3, 0, 2 })
                {
                    call.Data(Part(run, part), $"\"{run}:{part}\"");
                }

                return (call, "step");
            default:
                return (call.Data(Part(run, 1), $"\"{run}:1\"").Data(Part(run, 0), $"\"{run}:0\"").DisableImmediateExecution(), Call.Hash(Part(run, 2)));
        }
    }

    private static (Call Call, string StepId) OffSchedulerCall(int run)
    {
        var call = Call.New().Event(Event(run));
        switch (run % 4)
        {
            case 0:
                return (call, "step");
            case 1:
                return (call.Data($"first-{run}", $"\"{run}:first\""), "step");
            case 2:
                call.Data($"first-{run}", $"\"{run}:first\"");
                foreach (var part in new[] { 2, 0, 1 })
                {
                    call.Data(Part(run, part), $"\"{run}:{part}\"");
                }

                return (call, "step");
            default:
                return (call.Data($"first-{run}", $"\"{run}:first\"").Data(Part(run, 0), $"\"{run}:0\"").Data(Part(run, 1), $"\"{run}:1\"").DisableImmediateExecution(), Call.Hash(Part(run, 2)));
        }
    }

    private static string Part(int run, int part) => $"part-{run}-{part}";

    private static string Event(int run) =>
        $$$"""{"data":{"run":{{{run}}}},"id":"01JSTRESS{{{run:D8}}}","name":"test/event","ts":1793268000000,"user":{}}""";

    // Answers each send with an id made from the run number in its event, so that a body sent for the wrong
    // invocation shows up in the ids that invocation gets back.
    private sealed class EventApi : HttpMessageHandler
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var ids = body.RootElement.EnumerateArray().Select(static e => $"\"event-{e.GetProperty("data").GetProperty("run").GetInt32()}\"");
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"ids":[{{string.Join(',', ids)}}],"status":200}"""),
            };
        }
    }
}
