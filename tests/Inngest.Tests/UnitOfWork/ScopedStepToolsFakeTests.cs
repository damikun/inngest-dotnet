using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Tests.UnitOfWork;

// Users fake IStepTools. The scoped members have defaults, so a fake written before them still compiles and refuses
// them by name, and a fake that implements the one member everything else is built on gets all the overloads.
public sealed class ScopedStepToolsFakeTests
{
    private const string Refusal = "UnusedStepTools does not implement RunScopedAsync.";

    [Fact]
    public void A_fake_that_predates_scoped_steps_refuses_every_overload_by_name()
    {
        IStepTools step = new UnusedStepTools();

        Should.Throw<NotSupportedException>(() => step.RunScopedAsync("s", (_, _) => Task.FromResult(1))).Message.ShouldBe(Refusal);
        Should.Throw<NotSupportedException>(() => step.RunScopedAsync(new StepOptions("s"), (_, _) => Task.FromResult(1))).Message.ShouldBe(Refusal);
        Should.Throw<NotSupportedException>(() => step.RunScopedAsync("s", (_, _) => Task.CompletedTask)).Message.ShouldBe(Refusal);
        Should.Throw<NotSupportedException>(() => step.RunScopedAsync(new StepOptions("s"), (_, _) => Task.CompletedTask)).Message.ShouldBe(Refusal);
        Should.Throw<NotSupportedException>(() => step.RunScopedAsync("s", (Probe _, CancellationToken _) => Task.FromResult(1))).Message.ShouldBe(Refusal);
        Should.Throw<NotSupportedException>(() => step.RunScopedAsync(new StepOptions("s"), (Probe _, CancellationToken _) => Task.FromResult(1)))
            .Message.ShouldBe(Refusal);
        Should.Throw<NotSupportedException>(() => step.RunScopedAsync("s", (Probe _, CancellationToken _) => Task.CompletedTask)).Message.ShouldBe(Refusal);
        Should.Throw<NotSupportedException>(() => step.RunScopedAsync(new StepOptions("s"), (Probe _, CancellationToken _) => Task.CompletedTask))
            .Message.ShouldBe(Refusal);
    }

    [Fact]
    public async Task A_fake_that_implements_the_one_member_gets_the_other_overloads()
    {
        var scopes = new ScopeLog();
        await using var provider = new ServiceCollection().AddProbes(scopes).BuildServiceProvider();
        var fake = new ScopingStepTools(provider);
        IStepTools step = fake;

        (await step.RunScopedAsync("by-provider", (services, _) => Task.FromResult(services.GetRequiredService<Probe>().Number))).ShouldBe(1);
        (await step.RunScopedAsync(new StepOptions("named"), (services, _) => Task.FromResult(services.GetRequiredService<Probe>().Number))).ShouldBe(2);
        (await step.RunScopedAsync("typed", (Probe probe, CancellationToken _) => Task.FromResult(probe.Number))).ShouldBe(3);
        (await step.RunScopedAsync(new StepOptions("typed-named"), (Probe probe, CancellationToken _) => Task.FromResult(probe.Number))).ShouldBe(4);
        await step.RunScopedAsync("no-result", (services, _) => Touch(services.GetRequiredService<Probe>()));
        await step.RunScopedAsync(new StepOptions("no-result-named"), (services, _) => Touch(services.GetRequiredService<Probe>()));
        await step.RunScopedAsync<Probe>("typed-no-result", (probe, _) => Touch(probe));
        await step.RunScopedAsync<Probe>(new StepOptions("typed-no-result-named"), (probe, _) => Touch(probe));

        fake.Steps.ShouldBe(
            ["by-provider", "named", "typed", "typed-named", "no-result", "no-result-named", "typed-no-result", "typed-no-result-named"]);
        scopes.Created.Count.ShouldBe(8);
        scopes.Created.ShouldAllBe(probe => probe.IsDisposed);
    }

    [Fact]
    public void The_overloads_check_their_handler_before_they_reach_the_fake()
    {
        IStepTools step = new UnusedStepTools();

        Should.Throw<ArgumentNullException>(() => step.RunScopedAsync("s", (Func<IServiceProvider, CancellationToken, Task>)null!));
        Should.Throw<ArgumentNullException>(() => step.RunScopedAsync(new StepOptions("s"), (Func<IServiceProvider, CancellationToken, Task>)null!));
        Should.Throw<ArgumentNullException>(() => step.RunScopedAsync("s", (Func<Probe, CancellationToken, Task<int>>)null!));
        Should.Throw<ArgumentNullException>(() => step.RunScopedAsync(new StepOptions("s"), (Func<Probe, CancellationToken, Task<int>>)null!));
        Should.Throw<ArgumentNullException>(() => step.RunScopedAsync("s", (Func<Probe, CancellationToken, Task>)null!));
        Should.Throw<ArgumentNullException>(() => step.RunScopedAsync(new StepOptions("s"), (Func<Probe, CancellationToken, Task>)null!));
    }

    private static Task Touch(Probe probe)
    {
        probe.Entries.Add("touched");
        return Task.CompletedTask;
    }

    // A fake written against the six original members.
    private class UnusedStepTools : IStepTools
    {
        public Task<T> RunAsync<T>(string id, Func<CancellationToken, Task<T>> handler) => throw new NotSupportedException();

        public Task SleepAsync(string id, TimeSpan duration) => throw new NotSupportedException();

        public Task SleepUntilAsync(string id, DateTimeOffset until) => throw new NotSupportedException();

        public Task<InngestEvent<TData>?> WaitForEventAsync<TData>(string id, WaitForEventOptions options) => throw new NotSupportedException();

        public Task<TResult> InvokeAsync<TResult>(string id, InvokeOptions options) => throw new NotSupportedException();

        public Task<SendEventsResult> SendEventAsync(string id, IReadOnlyList<InngestEvent> events) => throw new NotSupportedException();
    }

    // Runs each handler in a scope of the test's own services, which is all a fake needs to do.
    private sealed class ScopingStepTools(IServiceProvider services) : UnusedStepTools, IStepTools
    {
        public List<string> Steps { get; } = [];

        public async Task<T> RunScopedAsync<T>(string id, Func<IServiceProvider, CancellationToken, Task<T>> handler)
        {
            Steps.Add(id);
            await using var scope = services.CreateAsyncScope();
            return await handler(scope.ServiceProvider, CancellationToken.None);
        }
    }
}
