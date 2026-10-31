using Inngest.Tests.Checkpointing;
using Inngest.Tests.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Tests.UnitOfWork;

// What a call can hold several of: steps that run one after another under checkpointing, which share the services of
// the call unless they ask for a scope of their own. And steps that Inngest runs at the same time in calls of their own,
// which can be on one app instance.
public sealed class ScopedStepCallTests : IDisposable
{
    private readonly ScopeLog _scopes = new();
    private readonly EngineHost _host;

    public ScopedStepCallTests() => _host = new EngineHost(services => services.AddProbes(_scopes));

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Each_step_of_a_checkpointed_call_gets_a_scope_of_its_own()
    {
        var function = Checkpointed.Define(async context =>
        {
            // The call's own instance, which comes first.
            context.Services.GetRequiredService<Probe>();
            var numbers = new List<int>();
            for (var i = 0; i < 3; i++)
            {
                numbers.Add(await context.Step.RunScopedAsync("work", (services, _) => Task.FromResult(services.GetRequiredService<Probe>().Number)));
            }

            return numbers;
        });

        var result = await _host.CallCheckpointedInScopeAsync(function, Call.New());

        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe("[2,3,4]");
        _host.Checkpoints.Checkpoints.Count.ShouldBe(3);

        // Each scope is gone before the next step starts, and the call's before nothing else is left.
        _scopes.Timeline.ShouldBe(["create 1", "create 2", "dispose 2", "create 3", "dispose 3", "create 4", "dispose 4", "dispose 1"]);
    }

    [Fact]
    public async Task Steps_that_use_the_service_of_the_call_share_what_it_tracks_which_a_scope_per_step_prevents()
    {
        var shared = Checkpointed.Define(async context =>
        {
            var probe = context.Services.GetRequiredService<Probe>();
            var counts = new List<int>();
            for (var i = 0; i < 3; i++)
            {
                counts.Add(await context.Step.RunAsync("work", () =>
                {
                    probe.Entries.Add("tracked");
                    return probe.Entries.Count;
                }));
            }

            return counts;
        });
        var scoped = Checkpointed.Define(async context =>
        {
            var counts = new List<int>();
            for (var i = 0; i < 3; i++)
            {
                counts.Add(await context.Step.RunScopedAsync("work", (Probe probe, CancellationToken _) =>
                {
                    probe.Entries.Add("tracked");
                    return Task.FromResult(probe.Entries.Count);
                }));
            }

            return counts;
        });

        var sharedResult = await _host.CallCheckpointedInScopeAsync(shared, Call.New());
        var scopedResult = await _host.CallCheckpointedInScopeAsync(scoped, Call.New());

        sharedResult.Body.ShouldBe("[1,2,3]");
        scopedResult.Body.ShouldBe("[1,1,1]");
    }

    [Fact]
    public async Task A_disposal_failure_names_the_step_by_its_final_id()
    {
        // The second use of an id in a loop is numbered, as in every other log of a step.
        var function = Checkpointed.Define(async context =>
        {
            for (var i = 0; i < 2; i++)
            {
                var failing = i == 1;
                await context.Step.RunScopedAsync("record", (services, _) =>
                {
                    if (failing)
                    {
                        services.GetRequiredService<ThrowingProbe>();
                    }

                    return Task.CompletedTask;
                });
            }

            return null;
        });

        var result = await _host.CallCheckpointedInScopeAsync(function, Call.New());

        result.StatusCode.ShouldBe(200);
        var record = _host.LogsOf("Inngest.Function.fn").ShouldHaveSingleItem();
        record.Message.ShouldBe("Disposing the services of step record:1 of run 01JRUN of function fn failed; the step's outcome stands.");
    }

    [Fact]
    public async Task Every_overload_runs_in_a_scope_of_its_own()
    {
        var function = Checkpointed.Define(async context =>
        {
            var step = context.Step;
            var named = new StepOptions("named") { Name = "Named" };
            var results = new List<int>
            {
                await step.RunScopedAsync("by-provider", (services, _) => Task.FromResult(services.GetRequiredService<Probe>().Number)),
                await step.RunScopedAsync(named, (services, _) => Task.FromResult(services.GetRequiredService<Probe>().Number)),
                await step.RunScopedAsync("typed", (Probe probe, CancellationToken _) => Task.FromResult(probe.Number)),
                await step.RunScopedAsync(new StepOptions("typed-named"), (Probe probe, CancellationToken _) => Task.FromResult(probe.Number)),
            };

            await step.RunScopedAsync("no-result", (IServiceProvider services, CancellationToken _) => Track(services.GetRequiredService<Probe>()));
            await step.RunScopedAsync(new StepOptions("no-result-named"), (services, _) => Track(services.GetRequiredService<Probe>()));
            await step.RunScopedAsync("typed-no-result", (Probe probe, CancellationToken _) => Track(probe));
            await step.RunScopedAsync(new StepOptions("typed-no-result-named"), (Probe probe, CancellationToken _) => Track(probe));
            return results;
        });

        var result = await _host.CallCheckpointedInScopeAsync(function, Call.New());

        result.Body.ShouldBe("[1,2,3,4]");
        _scopes.Created.Count.ShouldBe(8);
        _scopes.Created.ShouldAllBe(probe => probe.IsDisposed);
        _scopes.Timeline.Count(entry => entry.StartsWith("create", StringComparison.Ordinal)).ShouldBe(8);
        _scopes.Created.Skip(4).ShouldAllBe(probe => probe.Entries.Count == 1);
    }

    private static Task Track(Probe probe)
    {
        probe.Entries.Add("x");
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Steps_that_run_at_the_same_time_never_share_a_scope()
    {
        // Both handlers are inside their scopes before either finishes, so the two overlap.
        var inside = new CountdownEvent(2);
        var function = TestFunction.Define(async context => await Task.WhenAll(
            context.Step.RunScopedAsync("a", (services, _) => Overlap(services)),
            context.Step.RunScopedAsync("b", (services, _) => Overlap(services))));

        async Task<int> Overlap(IServiceProvider services)
        {
            var probe = services.GetRequiredService<Probe>();
            inside.Signal();
            await Task.Run(() => inside.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
            probe.IsDisposed.ShouldBeFalse();
            return probe.Number;
        }

        var calls = await Task.WhenAll(
            RunAsync(Call.Hash("a")),
            RunAsync(Call.Hash("b")));

        // Both calls are handed the same provider, as a pair of calls on one app instance could be.
        calls.Select(call => call.OpCodes.Single()).ShouldBe(["StepRun", "StepRun"]);
        var numbers = calls.Select(call => call.Op().GetProperty("data").GetInt32()).Order().ToArray();
        numbers.ShouldBe([1, 2]);
        _scopes.Created.ShouldAllBe(probe => probe.IsDisposed);

        Task<CallResult> RunAsync(string stepId) => EngineHost.ResultOf(_host.Start(function, Call.New().DisableImmediateExecution(), stepId));
    }
}
