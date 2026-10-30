using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.Steps;

// Stands in for a DbContext: a scoped service that is made once per scope and records when it is disposed.
internal sealed class ScopedWork : IDisposable
{
    private TestRecording? _observed;

    public Guid Id { get; } = Guid.NewGuid();

    // Where to record the disposal, which the scope does when the instance's own scope ends.
    public ScopedWork Observe(TestRecording observed)
    {
        _observed = observed;
        return this;
    }

    public void Dispose() => _observed?.Record("disposed", Id);
}

// Steps that run in a scope of their own, over the request scope of a real call from the server.
internal static class ScopedStepFunctions
{
    public const string Scoped = "steps/scoped";

    public static void Add(IInngestBuilder inngest)
    {
        inngest.Services.AddScoped<ScopedWork>();
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "scoped-steps", Retries = 2, Triggers = [Trigger.Event(Scoped)] },
            static async (context, _) =>
            {
                var observed = context.Observed();

                // The instance of the call, which no step may use.
                observed.Record("call", context.Services.GetRequiredService<ScopedWork>().Observe(observed).Id);

                await context.Step.RunScopedAsync("first", (ScopedWork work, CancellationToken _) =>
                {
                    observed.Record("first", work.Observe(observed).Id);
                    return Task.CompletedTask;
                });

                return await context.Step.RunScopedAsync("flaky", (ScopedWork work, CancellationToken _) =>
                {
                    observed.Record("flaky", work.Observe(observed).Id);
                    return context.Attempt == 0 ? throw new InvalidOperationException("the first attempt fails") : Task.FromResult(context.Attempt);
                });
            });
    }
}
