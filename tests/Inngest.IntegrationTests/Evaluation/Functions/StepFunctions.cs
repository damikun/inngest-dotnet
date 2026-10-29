using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Inngest.IntegrationTests.Evaluation.Functions;

internal sealed record ChainResult(int Steps, int Total);

internal sealed record IoResult(string A, string B, string FileText, int HealthStatus);

internal sealed record CaughtResult(bool Caught, string? StepId, string? ErrorName, string? Message, string? Recovery);

internal sealed record AsyncStepResult(string Text, bool TokenCanceled, double ElapsedMilliseconds);

internal sealed record OutputKindEvent(string TestId, string Kind) : IScenarioEvent;

// Sequences of steps, steps in loops, code between steps, and steps that fail and are caught.
internal static class StepFunctions
{
    public const string Chain = "eval/step.chain";
    public const string Loop = "eval/step.loop";
    public const string Io = "eval/step.io";
    public const string Catch = "eval/step.catch";
    public const string CatchNonRetriable = "eval/step.catch-non-retriable";
    public const string AsyncHandler = "eval/step.async";
    public const string Unawaited = "eval/step.unawaited";
    public const string OutputKind = "eval/step.output-kind";
    public const string SuffixIds = "eval/step.suffix-ids";

    public static void Add(IInngestBuilder inngest)
    {
        // Each step adds its index to the total of the steps before it.
        inngest.AddFunction<ValueEvent>(
            new FunctionOptions { Id = "step-chain", Triggers = [Trigger.Event(Chain)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                var total = 0;
                for (var i = 1; i <= context.Event.Data.Value; i++)
                {
                    var index = i;
                    var before = total;
                    total = await context.Step.RunAsync($"step-{index}", () =>
                    {
                        observed.Record($"step-{index}");
                        return before + index;
                    });
                }

                return new ChainResult(context.Event.Data.Value, total);
            });

        // "item:1" is also what the second use of "item" would be called, so the SDK has to skip to "item:2".
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "step-suffix-ids", Triggers = [Trigger.Event(SuffixIds)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                var explicitId = await context.Step.RunAsync("item:1", () =>
                {
                    observed.Record("explicit");
                    return "explicit";
                });
                var first = await context.Step.RunAsync("item", () =>
                {
                    observed.Record("first");
                    return "first";
                });
                var second = await context.Step.RunAsync("item", () =>
                {
                    observed.Record("second");
                    return "second";
                });

                return new[] { explicitId, first, second };
            });

        // The same id in every iteration; the SDK tells the calls apart by order.
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "step-loop", Triggers = [Trigger.Event(Loop)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                var squares = new List<int>();
                for (var i = 0; i < 5; i++)
                {
                    var value = i;
                    squares.Add(await context.Step.RunAsync("square", () =>
                    {
                        observed.Record("square", value);
                        return value * value;
                    }));
                }

                return squares;
            });

        // Real I/O between steps, outside of any step: a timer, a file and an HTTP call. It runs again on every replay.
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "step-io", Triggers = [Trigger.Event(Io)] },
            static async (context, cancellationToken) =>
            {
                var observed = context.Observed();
                var a = await context.Step.RunAsync("a", () =>
                {
                    observed.Record("a");
                    return "A";
                });

                observed.Record("between");
                await Task.Delay(200, cancellationToken);
                var path = Path.Combine(Path.GetTempPath(), $"inngest-eval-{context.Event.Data.TestId}.txt");
                await File.WriteAllTextAsync(path, $"{a} written", cancellationToken);
                var fileText = await File.ReadAllTextAsync(path, cancellationToken);
                File.Delete(path);
                var devServer = context.Services.GetRequiredService<IOptions<InngestOptions>>().Value.DevServerUrl!;
                using var health = await context.Services.GetRequiredService<IHttpClientFactory>().CreateClient()
                    .GetAsync(new Uri(devServer, "/health"), cancellationToken);

                var b = await context.Step.RunAsync("b", () =>
                {
                    observed.Record("b");
                    return $"{fileText} then B";
                });

                return new IoResult(a, b, fileText, (int)health.StatusCode);
            });

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "step-catch", Retries = 1, Triggers = [Trigger.Event(Catch)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                try
                {
                    await context.Step.RunAsync("charge", () =>
                        observed.RecordThenThrow("charge", context.Attempt, new InvalidOperationException("card declined")));

                    return new CaughtResult(false, null, null, null, null);
                }
                catch (StepFailedException exception)
                {
                    var recovery = await context.Step.RunAsync("refund", () =>
                    {
                        observed.Record("refund");
                        return "refunded";
                    });

                    return new CaughtResult(true, exception.StepId, exception.Error.Name, exception.Message, recovery);
                }
            });

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "step-catch-non-retriable", Retries = 3, Triggers = [Trigger.Event(CatchNonRetriable)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                try
                {
                    await context.Step.RunAsync("validate", () =>
                        observed.RecordThenThrow("validate", context.Attempt, new NonRetriableException("the order is invalid")));

                    return new CaughtResult(false, null, null, null, null);
                }
                catch (StepFailedException exception)
                {
                    return new CaughtResult(true, exception.StepId, exception.Error.Name, exception.Message, null);
                }
            });

        // A step body with awaits of its own; the handler gets the call's cancellation token.
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "step-async", Triggers = [Trigger.Event(AsyncHandler)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                return await context.Step.RunAsync("work", async cancellationToken =>
                {
                    observed.Record("work");
                    var started = DateTimeOffset.UtcNow;
                    await Task.Delay(150, cancellationToken);
                    await Task.Yield();
                    var text = await Task.Run(() => "computed off the request thread", cancellationToken);
                    await Task.Delay(150, cancellationToken);
                    return new AsyncStepResult(text, cancellationToken.IsCancellationRequested, (DateTimeOffset.UtcNow - started).TotalMilliseconds);
                });
            });

        // Starts a step and returns without awaiting it.
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "step-unawaited", Triggers = [Trigger.Event(Unawaited)] },
            static (context, cancellationToken) =>
            {
                var observed = context.Observed();
                _ = context.Step.RunAsync("side-effect", () => observed.Record("side-effect"));
                return Task.FromResult<object?>("returned without waiting");
            });

        inngest.AddFunction<OutputKindEvent>(
            new FunctionOptions { Id = "step-output-kind", Triggers = [Trigger.Event(OutputKind)] },
            static (context, _) => Task.FromResult<object?>(context.Event.Data.Kind switch
            {
                "text" => "plain text",
                "number" => 42.5,
                "array" => new[] { 1, 2, 3 },
                "element" => JsonDocument.Parse("""{"raw":true}""").RootElement,
                _ => null,
            }));
    }
}
