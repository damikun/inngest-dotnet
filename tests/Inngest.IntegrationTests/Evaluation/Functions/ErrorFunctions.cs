using Inngest.IntegrationTests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.Evaluation.Functions;

internal sealed record AttemptSeen(int Attempt, int? MaxAttempts);

internal sealed record HandledFailure(string TriggerName, string FunctionId, string RunId, string ErrorName, string ErrorMessage, string? OriginalEvent);

internal sealed record ParallelFailure(string FailedStep, string Message, int First, int Last);

internal sealed class PaymentDeclinedException(string message, Exception innerException) : Exception(message, innerException);

// Refers to itself, which JSON cannot represent.
internal sealed class Loop
{
    public string Name { get; set; } = "loop";

    public Loop? Next { get; set; }
}

internal static class RetryFunctions
{
    public const string Eventual = "eval/err.eventual";
    public const string Exhausted = "eval/err.exhausted";
    public const string NoRetries = "eval/err.no-retries";
    public const string StepNonRetriable = "eval/err.step-non-retriable";
    public const string FunctionNonRetriable = "eval/err.function-non-retriable";
    public const string RetryAt = "eval/err.retry-at";
    public const string StepRetryAfter = "eval/err.step-retry-after";
    public const string StepBudget = "eval/err.step-budget";

    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    public static void Add(IInngestBuilder inngest)
    {
        // No steps, so every call is one attempt of the function.
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "err-eventual", Retries = 3, Triggers = [Trigger.Event(Eventual)] },
            static (context, _) =>
            {
                context.Observed().Record("attempt", new AttemptSeen(context.Attempt, context.MaxAttempts));
                return context.Attempt < 2
                    ? throw new InvalidOperationException($"attempt {context.Attempt} failed")
                    : Task.FromResult<object?>(context.Attempt);
            });

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "err-exhausted", Retries = 2, Triggers = [Trigger.Event(Exhausted)] },
            static (context, _) =>
            {
                context.Observed().Record("attempt", context.Attempt);
                throw new InvalidOperationException($"attempt {context.Attempt} failed");
            });

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "err-no-retries", Retries = 0, Triggers = [Trigger.Event(NoRetries)] },
            static (context, _) =>
            {
                context.Observed().Record("attempt", context.Attempt);
                throw new InvalidOperationException("no second chance");
            });

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "err-step-non-retriable", Retries = 3, Triggers = [Trigger.Event(StepNonRetriable)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                return await context.Step.RunAsync("validate", () =>
                    observed.RecordThenThrow("validate", context.Attempt, new NonRetriableException("the input is unusable")));
            });

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "err-function-non-retriable", Retries = 3, Triggers = [Trigger.Event(FunctionNonRetriable)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                await context.Step.RunAsync("done", () => observed.Record("done"));
                observed.Record("throw", context.Attempt);
                throw new NonRetriableException("gave up after the step");
            });

        // The first execution asks for the retry, whichever attempt it was.
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "err-retry-at", Retries = 3, Triggers = [Trigger.Event(RetryAt)] },
            static (context, _) =>
                context.Observed().Record("attempt", context.Attempt) == 1
                    ? throw new RetryAfterException("not before the given time", DateTimeOffset.UtcNow + RetryDelay)
                    : Task.FromResult<object?>(context.Attempt));

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "err-step-retry-after", Retries = 3, Triggers = [Trigger.Event(StepRetryAfter)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                return await context.Step.RunAsync("throttled", () =>
                    observed.Record("attempt", context.Attempt) == 1 ? throw new RetryAfterException("rate limited", RetryDelay) : "through");
            });

        // One retry per step: three steps that each need it only pass if every step has a budget of its own, and each
        // step counts its attempts from zero.
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "err-step-budget", Retries = 1, Triggers = [Trigger.Event(StepBudget)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                foreach (var step in new[] { "a", "b", "c" })
                {
                    await context.Step.RunAsync(step, () => FailFirstAttempt(observed, step, context.Attempt));
                }

                return "all three recovered";
            });
    }

    private static int FailFirstAttempt(TestRecording observed, string step, int attempt)
    {
        observed.Record(step, attempt);
        return attempt == 0 ? throw new InvalidOperationException($"{step} fails its first attempt") : attempt;
    }
}

internal static class FailureFunctions
{
    public const string Details = "eval/err.details";
    public const string Handled = "eval/err.handled";
    public const string HandledWithSteps = "eval/err.handled-steps";
    public const string ParallelFailure = "eval/err.parallel";
    public const string Unserializable = "eval/err.unserializable";

    public static void Add(IInngestBuilder inngest)
    {
        // Retries to spare, so that the output is this exception even if the server could not deliver a call.
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "err-details", Retries = 2, Triggers = [Trigger.Event(Details)] },
            static (context, _) => throw new PaymentDeclinedException(
                "card 4242 was declined",
                new TimeoutException("the payment gateway timed out")));

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "err-handled", Retries = 1, Triggers = [Trigger.Event(Handled)] },
            static (context, _) =>
            {
                context.Observed().Record("attempt", context.Attempt);
                throw new InvalidOperationException($"handled failure {context.Event.Data.TestId}");
            },
            onFailure: static (context, _) =>
            {
                var failure = context.Event.Data;
                var original = failure.Event;
                Recording(context, failure).Record("on-failure", new HandledFailure(
                    context.Event.Name,
                    failure.FunctionId,
                    failure.RunId,
                    failure.Error.Name,
                    failure.Error.Message,
                    original.TryGetProperty("name", out var name) ? name.GetString() : null));
                return Task.CompletedTask;
            });

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "err-handled-steps", Retries = 0, Triggers = [Trigger.Event(HandledWithSteps)] },
            static (context, _) => throw new InvalidOperationException("fails so the handler runs"),
            onFailure: static async (context, _) =>
            {
                var observed = Recording(context, context.Event.Data);
                var notified = await context.Step.RunAsync("notify", () => observed.Record("notify"));
                await context.Step.SleepAsync("settle", TimeSpan.FromSeconds(1));
                await context.Step.RunAsync("cleanup", () => observed.Record("cleanup", notified));
            });

        // The step body succeeds, but its result cannot be saved, which fails the step like an exception would.
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "err-unserializable", Retries = 1, Triggers = [Trigger.Event(Unserializable)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                try
                {
                    await context.Step.RunAsync("cyclic", () =>
                    {
                        observed.Record("cyclic", context.Attempt);
                        var loop = new Loop();
                        loop.Next = loop;
                        return loop;
                    });
                    return new CaughtResult(false, null, null, null, null);
                }
                catch (StepFailedException exception)
                {
                    return new CaughtResult(true, exception.StepId, exception.Error.Name, exception.Message, null);
                }
            });

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "err-parallel", Retries = 2, Triggers = [Trigger.Event(ParallelFailure)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                var first = context.Step.RunAsync("first", () => observed.Record("first"));
                var broken = context.Step.RunAsync("broken", () => observed.RecordThenThrow("broken", null, new NonRetriableException("broken for good")));
                var last = context.Step.RunAsync("last", () => observed.Record("last"));
                try
                {
                    await Task.WhenAll(first, broken, last);
                    return null;
                }
                catch (StepFailedException exception)
                {
                    return new ParallelFailure(exception.StepId, exception.Message, await first, await last);
                }
            });
    }

    // A failure handler runs for the inngest/function.failed event, whose data carries the original event.
    private static TestRecording Recording(FunctionContext context, FunctionFailure failure) =>
        context.Services.GetRequiredService<Probe>().For(failure.Event.GetProperty("data").GetProperty("testId").GetString()!);
}
