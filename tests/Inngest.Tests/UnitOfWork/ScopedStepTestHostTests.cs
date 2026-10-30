using System.Text.Json;
using Inngest.Testing;
using Microsoft.Extensions.DependencyInjection;
using static Inngest.Tests.Testing.TestHosts;

namespace Inngest.Tests.UnitOfWork;

// Inngest.Testing replays a function on the real engine, so scoped steps work there as under Inngest, and a run mock
// answers one where its handler would run.
public sealed class ScopedStepTestHostTests
{
    private readonly ScopeLog _scopes = new();

    [Fact]
    public async Task The_test_host_gives_each_scoped_step_a_scope_and_a_replay_makes_none()
    {
        await using var host = Host(async context =>
        {
            var first = await context.Step.RunScopedAsync("first", (services, _) => Task.FromResult(services.GetRequiredService<Probe>().Number));
            var second = await context.Step.RunScopedAsync("second", (Probe probe, CancellationToken _) => Task.FromResult(probe.Number));
            return new { first, second };
        });

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        var output = run.Output!.Value;
        output.GetProperty("first").GetInt32().ShouldBe(1);
        output.GetProperty("second").GetInt32().ShouldBe(2);
        run.Steps.ShouldAllBe(step => step.Type == StepType.Run && step.Attempts == 1 && !step.Mocked);

        // The function ran three times, and only the two steps that ran made scopes.
        _scopes.Timeline.ShouldBe(["create 1", "dispose 1", "create 2", "dispose 2"]);
    }

    [Fact]
    public async Task A_run_mock_answers_a_scoped_step_without_making_a_scope()
    {
        await using var host = Host(async context =>
            await context.Step.RunScopedAsync("work", (services, _) => Task.FromResult(services.GetRequiredService<Probe>().Number)));

        var run = await host.RunAsync("fn", Event(), new StepMocks().Run("work", 42), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        run.GetOutput<int>().ShouldBe(42);
        run.GetStep("work").Mocked.ShouldBeTrue();
        _scopes.Timeline.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_mock_that_fails_the_first_attempts_fails_a_scoped_step_like_any_run_step()
    {
        await using var host = Host(async context =>
            await context.Step.RunScopedAsync("work", (services, _) => Task.FromResult(services.GetRequiredService<Probe>().Number)));

        var run = await host.RunAsync("fn", Event(), new StepMocks().Run("work", attempt => attempt < 2 ? throw new InvalidOperationException("flaky") : 7), Cancel);

        run.GetOutput<int>().ShouldBe(7);
        run.GetStep("work").Attempts.ShouldBe(3);
        _scopes.Timeline.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_retried_scoped_step_gets_a_scope_for_every_attempt()
    {
        await using var host = Host(async context => await context.Step.RunScopedAsync("work", (services, _) =>
        {
            var probe = services.GetRequiredService<Probe>();
            return context.Attempt < 2 ? throw new InvalidOperationException("flaky") : Task.FromResult(probe.Number);
        }));

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        run.GetOutput<int>().ShouldBe(3);
        run.GetStep("work").Attempts.ShouldBe(3);
        run.GetStep("work").Errors.Select(error => error.Message).ShouldBe(["flaky", "flaky"]);
        _scopes.Timeline.ShouldBe(["create 1", "dispose 1", "create 2", "dispose 2", "create 3", "dispose 3"]);
    }

    [Fact]
    public async Task Scoped_steps_that_start_together_each_run_in_a_scope_of_their_own()
    {
        await using var host = Host(async context => await Task.WhenAll(
            context.Step.RunScopedAsync("a", (services, _) => Task.FromResult(services.GetRequiredService<Probe>().Number)),
            context.Step.RunScopedAsync("b", (services, _) => Task.FromResult(services.GetRequiredService<Probe>().Number))));

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.GetOutput<int[]>()!.Order().ShouldBe([1, 2]);
        _scopes.Created.ShouldAllBe(probe => probe.IsDisposed);
    }

    [Fact]
    public async Task The_scope_of_the_step_is_not_the_scope_of_the_call()
    {
        await using var host = Host(async context =>
        {
            var inCall = context.Services.GetRequiredService<Probe>();
            var inStep = await context.Step.RunScopedAsync("work", (services, _) => Task.FromResult(services.GetRequiredService<Probe>().Number));
            return new { call = inCall.Number, step = inStep, callAgain = context.Services.GetRequiredService<Probe>().Number };
        });

        var run = await host.RunAsync("fn", Event(), Cancel);

        // Each of the two calls makes its own instance for the function, which is one instance within the call.
        var output = run.Output!.Value;
        output.GetProperty("call").GetInt32().ShouldBe(output.GetProperty("callAgain").GetInt32());
        output.GetProperty("step").GetInt32().ShouldNotBe(output.GetProperty("call").GetInt32());
    }

    private InngestTestHost Host(Func<FunctionContext<JsonElement>, Task<object?>> handler)
    {
        var services = Services(handler);
        services.AddProbes(_scopes);
        return new InngestTestHost(services);
    }
}
