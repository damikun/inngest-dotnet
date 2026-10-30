using Inngest.Execution;
using Inngest.Tests.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Inngest.Tests.UnitOfWork;

// A scoped step runs its handler in a service scope of its own. The engine makes the scope when the handler starts and
// disposes it when the handler ends, so a replay, a plan and a mock make none, and every attempt makes one.
public sealed class ScopedStepTests : IDisposable
{
    private readonly ScopeLog _scopes = new();
    private readonly EngineHost _host;

    public ScopedStepTests() => _host = new EngineHost(services => services.AddProbes(_scopes));

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task A_scoped_step_runs_its_handler_in_a_scope_of_its_own()
    {
        var function = TestFunction.Define(async context =>
        {
            // The call's own instance, which comes first.
            context.Services.GetRequiredService<Probe>();
            return await context.Step.RunScopedAsync("work", (services, _) => Task.FromResult(services.GetRequiredService<Probe>().Number));
        });

        var result = await _host.CallInScopeAsync(function, Call.New());

        result.OpCodes.ShouldBe(["StepRun"]);
        result.Op().GetProperty("data").GetInt32().ShouldBe(2);

        // The step's scope is gone before the call's.
        _scopes.Timeline.ShouldBe(["create 1", "create 2", "dispose 2", "dispose 1"]);
    }

    [Fact]
    public async Task The_scope_lasts_until_the_handler_has_finished()
    {
        var disposedWhileRunning = true;
        var function = TestFunction.Define(async context => await context.Step.RunScopedAsync("work", async (services, _) =>
        {
            var probe = services.GetRequiredService<Probe>();
            await Task.Yield();
            disposedWhileRunning = probe.IsDisposed;
            return probe.Number;
        }));

        var result = await _host.CallInScopeAsync(function, Call.New());

        result.OpCodes.ShouldBe(["StepRun"]);
        disposedWhileRunning.ShouldBeFalse();
        _scopes.Created.ShouldHaveSingleItem().IsDisposed.ShouldBeTrue();
    }

    [Fact]
    public async Task The_scope_disposes_services_that_are_disposable_either_way()
    {
        var function = TestFunction.Define(async context => await context.Step.RunScopedAsync("work", (services, _) =>
        {
            services.GetRequiredService<Probe>();
            services.GetRequiredService<AsyncProbe>();
            return Task.FromResult(true);
        }));

        await _host.CallInScopeAsync(function, Call.New());

        _scopes.Created.Select(probe => probe.GetType().Name).ShouldBe(["Probe", "AsyncProbe"]);
        _scopes.Created.ShouldAllBe(probe => probe.IsDisposed);
    }

    [Fact]
    public async Task The_scope_is_disposed_when_the_call_is_canceled_during_the_handler()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var inside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var function = TestFunction.Define(async context => await context.Step.RunScopedAsync("work", async (services, ct) =>
        {
            services.GetRequiredService<Probe>();
            inside.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return 1;
        }));
        await using var scope = _host.Services.CreateAsyncScope();
        var request = new ExecutionRequest { Function = function, Body = Call.New().ToBytes(), StepId = "step", Services = scope.ServiceProvider };
        var call = _host.Engine.ExecuteAsync(request, cancellation.Token);

        await inside.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(call);
        _scopes.Timeline.ShouldBe(["create 1", "dispose 1"]);
    }

    [Fact]
    public async Task A_memoized_scoped_step_makes_no_scope()
    {
        var function = TestFunction.Define(async context =>
            await context.Step.RunScopedAsync("work", (services, _) => Task.FromResult(services.GetRequiredService<Probe>().Number)));

        var result = await _host.CallInScopeAsync(function, Call.New().Data("work", "7"));

        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe("7");
        _scopes.Timeline.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_planned_scoped_step_makes_no_scope_until_Inngest_runs_it()
    {
        var function = TestFunction.Define(async context => await Task.WhenAll(
            context.Step.RunScopedAsync("a", (services, _) => Task.FromResult(services.GetRequiredService<Probe>().Number)),
            context.Step.RunScopedAsync("b", (services, _) => Task.FromResult(services.GetRequiredService<Probe>().Number))));

        var planned = await _host.CallInScopeAsync(function, Call.New());
        var ran = await _host.CallInScopeAsync(function, Call.New().DisableImmediateExecution(), stepId: Call.Hash("b"));

        planned.OpCodes.ShouldBe(["StepPlanned", "StepPlanned"]);
        ran.OpCodes.ShouldBe(["StepRun"]);
        _scopes.Timeline.ShouldBe(["create 1", "dispose 1"]);
    }

    [Fact]
    public async Task Every_attempt_gets_a_scope_and_one_that_failed_is_disposed_too()
    {
        var function = TestFunction.Define(async context => await context.Step.RunScopedAsync("work", (services, _) =>
        {
            var probe = services.GetRequiredService<Probe>();
            return context.Attempt == 0 ? throw new InvalidOperationException("flaky") : Task.FromResult(probe.Number);
        }));

        var first = await _host.CallInScopeAsync(function, Call.New());
        var second = await _host.CallInScopeAsync(function, Call.New().Attempt(1));

        first.OpCodes.ShouldBe(["StepError"]);
        first.Op().GetProperty("error").GetProperty("message").GetString().ShouldBe("flaky");
        second.OpCodes.ShouldBe(["StepRun"]);
        second.Op().GetProperty("data").GetInt32().ShouldBe(2);
        _scopes.Timeline.ShouldBe(["create 1", "dispose 1", "create 2", "dispose 2"]);
    }

    [Fact]
    public async Task A_service_that_fails_to_dispose_does_not_fail_a_step_that_did_its_work()
    {
        var function = TestFunction.Define(async context => await context.Step.RunScopedAsync("work", (services, _) =>
        {
            services.GetRequiredService<ThrowingProbe>();
            return Task.FromResult("saved");
        }));

        var result = await _host.CallInScopeAsync(function, Call.New());

        // A failed step is retried, which would do the work again.
        result.OpCodes.ShouldBe(["StepRun"]);
        result.Op().GetProperty("data").GetString().ShouldBe("saved");
        var record = _host.LogsOf("Inngest.Function.fn").ShouldHaveSingleItem();
        record.Level.ShouldBe(LogLevel.Error);
        record.Id.Id.ShouldBe(150);
        record.Message.ShouldBe("Disposing the services of step work of run 01JRUN of function fn failed; the step's outcome stands.");
        record.Exception.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("disposal failed");
    }

    [Fact]
    public async Task A_service_that_fails_to_dispose_does_not_hide_the_error_of_the_handler()
    {
        var function = TestFunction.Define(async context => await context.Step.RunScopedAsync<string>("work", (services, _) =>
        {
            services.GetRequiredService<ThrowingProbe>();
            throw new InvalidOperationException("boom");
        }));

        var result = await _host.CallInScopeAsync(function, Call.New());

        result.OpCodes.ShouldBe(["StepError"]);
        result.Op().GetProperty("error").GetProperty("message").GetString().ShouldBe("boom");
        _host.LogsOf("Inngest.Function.fn").ShouldContain(record => record.Id.Id == 150);
    }

    [Fact]
    public async Task The_typed_overloads_resolve_the_service_from_the_scope_of_the_step()
    {
        var function = TestFunction.Define(async context =>
            await context.Step.RunScopedAsync("work", (Probe probe, CancellationToken _) => Task.FromResult(probe.Number)));

        var result = await _host.CallInScopeAsync(function, Call.New());

        result.Op().GetProperty("data").GetInt32().ShouldBe(1);
        _scopes.Timeline.ShouldBe(["create 1", "dispose 1"]);
    }

    [Fact]
    public async Task A_scoped_step_is_a_run_step_to_Inngest()
    {
        var scoped = TestFunction.Define(async context =>
            await context.Step.RunScopedAsync(new StepOptions("work") { Name = "Do the work" }, (_, _) => Task.FromResult("done")));
        var plain = TestFunction.Define(async context =>
            await context.Step.RunAsync(new StepOptions("work") { Name = "Do the work" }, () => "done"));

        var scopedPlan = await _host.CallInScopeAsync(scoped, Call.New().DisableImmediateExecution());
        var plainPlan = await _host.CallInScopeAsync(plain, Call.New().DisableImmediateExecution());
        var scopedRun = await _host.CallInScopeAsync(scoped, Call.New());
        var plainRun = await _host.CallInScopeAsync(plain, Call.New());

        scopedPlan.Body.ShouldBe(plainPlan.Body);
        scopedRun.Body.ShouldBe(plainRun.Body);
        scopedRun.Body.ShouldBe(
            $$$"""[{"id":"{{{Call.Hash("work")}}}","op":"StepRun","name":"work","displayName":"Do the work","opts":{"type":"step.run"},"userland":{"id":"work"},"data":"done","timing":{"a":{{{EngineHost.NowNanoseconds}}},"b":0}}]""");
    }

    [Fact]
    public async Task Middleware_sees_a_run_step_and_the_services_of_the_call_but_not_the_scope_of_the_step()
    {
        var observer = new ScopeObserver(_scopes);
        using var host = new EngineHost(services => services.AddProbes(_scopes).AddSingleton<InngestMiddleware>(observer));
        IServiceProvider? stepServices = null;
        var function = TestFunction.Define(async context => await context.Step.RunScopedAsync("work", (services, _) =>
        {
            stepServices = services;
            services.GetRequiredService<Probe>();
            return Task.FromResult(1);
        }));

        var result = await host.CallInScopeAsync(function, Call.New());

        result.OpCodes.ShouldBe(["StepRun"]);
        observer.Type.ShouldBe(StepType.Run);
        observer.CallServices.ShouldNotBeNull().ShouldNotBeSameAs(stepServices);

        // The scope is made inside next() and is disposed before it returns.
        observer.ScopesBeforeHandler.ShouldBe(0);
        observer.ScopesAfterHandler.ShouldBe(1);
        observer.DisposedAfterHandler.ShouldBeTrue();
    }

    [Fact]
    public async Task A_step_called_from_the_handler_of_a_scoped_step_is_refused_like_one_from_a_run_step()
    {
        var function = TestFunction.Define(async context => await context.Step.RunScopedAsync("outer", async (_, _) =>
        {
            await context.Step.RunAsync("inner", () => 1);
            return 1;
        }));

        var result = await _host.CallInScopeAsync(function, Call.New());

        result.OpCodes.ShouldBe(["StepError"]);
        result.Op().GetProperty("error").GetProperty("message").GetString()!.ShouldContain("inside the handler of step \"outer\"");
    }

    [Fact]
    public async Task Arguments_are_checked_when_the_step_is_called()
    {
        var function = TestFunction.Define(context =>
        {
            Should.Throw<ArgumentException>(() => context.Step.RunScopedAsync("", (_, _) => Task.FromResult(1)));
            Should.Throw<ArgumentException>(() => context.Step.RunScopedAsync(new StepOptions(""), (_, _) => Task.FromResult(1)));
            Should.Throw<ArgumentNullException>(() => context.Step.RunScopedAsync("work", (Func<IServiceProvider, CancellationToken, Task<int>>)null!));
            Should.Throw<ArgumentNullException>(() => context.Step.RunScopedAsync("work", (Func<IServiceProvider, CancellationToken, Task>)null!));
            Should.Throw<ArgumentNullException>(() => context.Step.RunScopedAsync("work", (Func<Probe, CancellationToken, Task<int>>)null!));
            Should.Throw<ArgumentNullException>(() => context.Step.RunScopedAsync("work", (Func<Probe, CancellationToken, Task>)null!));
            return Task.FromResult<object?>(null);
        });

        var result = await _host.CallInScopeAsync(function, Call.New());

        result.StatusCode.ShouldBe(200, result.Body);
        _scopes.Timeline.ShouldBeEmpty();
    }

    private sealed class ScopeObserver(ScopeLog scopes) : InngestMiddleware
    {
        public StepType? Type { get; private set; }

        public IServiceProvider? CallServices { get; private set; }

        public int ScopesBeforeHandler { get; private set; } = -1;

        public int ScopesAfterHandler { get; private set; } = -1;

        public bool DisposedAfterHandler { get; private set; }

        public override async Task<object?> WrapStepHandlerAsync(FunctionContext context, StepInfo step, Func<Task<object?>> next)
        {
            Type = step.Type;
            CallServices = context.Services;
            ScopesBeforeHandler = scopes.Created.Count;
            var result = await next();
            ScopesAfterHandler = scopes.Created.Count;
            DisposedAfterHandler = scopes.Created.All(probe => probe.IsDisposed);
            return result;
        }
    }
}
