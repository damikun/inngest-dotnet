using System.Text.Json;
using Inngest.IntegrationTests.Harness;
using Microsoft.Extensions.Logging;

namespace Inngest.IntegrationTests.Evaluation.Functions;

// A hook call as the recording middleware saw it. Instance identifies the middleware object, which the SDK resolves
// once per call.
internal sealed record Hook(string Name, string? Detail, int Attempt, string Instance);

internal sealed record Envelope(object? Output, string WrappedBy);

internal sealed record Transformed(string Shout, string Suffix);

internal static class MiddlewareFunctions
{
    public const string Observed = "eval/mw.observed";
    public const string Failing = "eval/mw.failing";
    public const string Transform = "eval/mw.transform";
    public const string Send = "eval/mw.send";
    public const string Stamped = "eval/mw.stamped";
    public const string Logging = "eval/mw.logging";
    public const string Audited = "eval/mw.audited";

    public const string TransformId = "mw-transform";
    public const string SendId = "mw-send";
    public const string AuditedId = "mw-audited";
    public const string Stamp = "added by middleware";

    public static void Add(IInngestBuilder inngest)
    {
        inngest.AddMiddleware<RecordingMiddleware>();
        inngest.AddMiddleware<TransformingMiddleware>();
        inngest.AddMiddleware<AuditingMiddleware>();

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "mw-observed", Triggers = [Trigger.Event(Observed)] },
            static async (context, _) =>
            {
                await context.Step.RunAsync("a", () => "A");
                await context.Step.RunAsync("b", () => "B");
                return "done";
            });

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "mw-failing", Retries = 1, Triggers = [Trigger.Event(Failing)] },
            static async (context, _) =>
                await context.Step.RunAsync<int>("flaky", _ => throw new InvalidOperationException("always fails")));

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = TransformId, Triggers = [Trigger.Event(Transform)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                var shout = await context.Step.RunAsync("shout", () =>
                {
                    observed.Record("shout-ran");
                    return "quiet words";
                });
                var suffix = await context.Step.RunAsync("suffix", () => "plain");
                observed.Record("seen", $"{shout} / {suffix}");
                return new Transformed(shout, suffix);
            });

        inngest.AddFunction<ValueEvent>(
            new FunctionOptions { Id = SendId, Triggers = [Trigger.Event(Send)] },
            static async (context, _) =>
                (await context.Step.SendEventAsync("notify", new InngestEvent(Stamped, context.Event.Data))).Ids);

        inngest.AddFunction<JsonElement>(
            new FunctionOptions { Id = "mw-receiver", Triggers = [Trigger.Event(Stamped)] },
            static (context, _) => Task.FromResult<object?>(context.Event.Data));

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = AuditedId, Triggers = [Trigger.Event(Audited)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                return await context.Step.RunAsync("work", () =>
                {
                    observed.Record("work");
                    return "worked";
                });
            });

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "mw-logging", Triggers = [Trigger.Event(Logging)] },
            static async (context, _) =>
            {
                var testId = context.Event.Data.TestId;
                context.Logger.LogInformation("start {TestId}", testId);
                await context.Step.RunAsync("a", () => 1);
                context.Logger.LogInformation("between {TestId}", testId);
                await context.Step.RunAsync("b", () => 2);
                context.Logger.LogWarning("end {TestId}", testId);
                return null;
            });
    }

    // Records every observer hook under the run id.
    private sealed class RecordingMiddleware(Probe probe) : InngestMiddleware
    {
        private readonly string _instance = Guid.NewGuid().ToString("N")[..8];

        public override ValueTask OnRunStartAsync(FunctionContext context) => Record(context, "run-start");

        public override ValueTask OnMemoizationEndAsync(FunctionContext context) => Record(context, "memoization-end");

        public override ValueTask OnRunCompleteAsync(FunctionContext context, object? output) => Record(context, "run-complete", output?.ToString());

        public override ValueTask OnRunErrorAsync(FunctionContext context, Exception exception, bool isFinalAttempt) =>
            Record(context, "run-error", isFinalAttempt ? "final" : "retry");

        public override ValueTask OnStepStartAsync(FunctionContext context, StepInfo step) => Record(context, "step-start", step.Id);

        public override ValueTask OnStepCompleteAsync(FunctionContext context, StepInfo step, object? output) =>
            Record(context, "step-complete", $"{step.Id}={output}");

        public override ValueTask OnStepErrorAsync(FunctionContext context, StepInfo step, Exception exception, bool isFinalAttempt) =>
            Record(context, "step-error", $"{step.Id}:{(isFinalAttempt ? "final" : "retry")}");

        private ValueTask Record(FunctionContext context, string hook, string? detail = null)
        {
            probe.For(context.RunId).Record(hook, new Hook(hook, detail, context.Attempt, _instance));
            return ValueTask.CompletedTask;
        }
    }

    // Runs steps of its own before and after the audited function, which memoize like the function's steps.
    private sealed class AuditingMiddleware(Probe probe) : InngestMiddleware
    {
        public override async Task<object?> WrapFunctionAsync(FunctionContext context, Func<Task<object?>> next)
        {
            if (context.FunctionId != AuditedId)
            {
                return await next();
            }

            var observed = probe.For(((FunctionContext<RunEvent>)context).Event.Data.TestId);
            var started = await context.Step.RunAsync("audit-start", () => observed.Record("audit-start"));
            var output = await next();
            await context.Step.RunAsync("audit-end", () => observed.Record("audit-end", started));
            return output;
        }
    }

    // Changes results for the functions above only; every other function passes through untouched.
    private sealed class TransformingMiddleware : InngestMiddleware
    {
        // Runs where the handler runs, so what it returns is what Inngest memoizes.
        public override async Task<object?> WrapStepHandlerAsync(FunctionContext context, StepInfo step, Func<Task<object?>> next)
        {
            var output = await next();
            return context.FunctionId == TransformId && step.Id == "shout" && output is string text ? text.ToUpperInvariant() : output;
        }

        // Runs whenever a result reaches the function, so it applies on every replay and is never memoized.
        public override async Task<object?> WrapStepAsync(FunctionContext context, StepInfo step, Func<Task<object?>> next)
        {
            var output = await next();
            return context.FunctionId == TransformId && step.Id == "suffix" && output is string text ? $"{text} (read by middleware)" : output;
        }

        public override async Task<object?> WrapFunctionAsync(FunctionContext context, Func<Task<object?>> next)
        {
            var output = await next();
            return context.FunctionId == TransformId ? new Envelope(output, "middleware") : output;
        }

        public override Task<SendEventsResult> WrapSendEventAsync(
            FunctionContext? context,
            IReadOnlyList<InngestEvent> events,
            Func<IReadOnlyList<InngestEvent>, Task<SendEventsResult>> next,
            CancellationToken cancellationToken)
        {
            if (context?.FunctionId != SendId)
            {
                return next(events);
            }

            return next([.. events.Select(@event =>
            {
                var data = JsonSerializer.SerializeToNode(@event.Data, JsonSerializerOptions.Web)!.AsObject();
                data["stamp"] = Stamp;
                return @event with { Data = data };
            })]);
        }
    }
}
