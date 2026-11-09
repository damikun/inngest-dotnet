using System.Text.Json;
using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.Execution;

internal sealed record CancelSeen(string TriggerName, string FunctionId, string RunId, string OriginalEventName);

internal sealed record CodeSeen(string Name, string? Code);

// Cancel handlers, as a class that implements ICancelHandler and as a delegate passed as onCancel.
internal static class CancelHandlerFunctions
{
    public const string Start = "exec/cancel.start";
    public const string Stop = "exec/cancel.stop";
    public const string StartDelegate = "exec/cancel-delegate.start";
    public const string StopDelegate = "exec/cancel-delegate.stop";

    public static void Add(IInngestBuilder inngest)
    {
        inngest.AddFunction<CancelledWithHandler>(CancelledWithHandler.Options);
        inngest.AddFunction<RunEvent>(
            new FunctionOptions
            {
                Id = "cancel-delegate",
                Triggers = [Trigger.Event(StartDelegate)],
                CancelOn = [new CancelOn(StopDelegate) { Match = "data.testId" }],
            },
            static async (context, _) =>
            {
                context.Observed().Record("started");
                await context.Step.SleepAsync("wait", TimeSpan.FromMinutes(5));
                return null;
            },
            onCancel: static (context, _) =>
            {
                Record(context);
                return Task.CompletedTask;
            });
    }

    // The cancelled run's event carries the test id.
    public static void Record(FunctionContext<FunctionCancelled> context)
    {
        var cancelled = context.Event.Data;
        var testId = cancelled.Event.GetProperty("data").GetProperty("testId").GetString()!;
        context.Services.GetRequiredService<Probe>().For(testId).Record("on-cancel", new CancelSeen(
            context.EventName,
            cancelled.FunctionId,
            cancelled.RunId,
            cancelled.Event.GetProperty("name").GetString()!));
    }
}

internal sealed class CancelledWithHandler : InngestFunction<RunEvent>, ICancelHandler
{
    public static FunctionOptions Options { get; } = new()
    {
        Id = "cancel-handled",
        Triggers = [Trigger.Event(CancelHandlerFunctions.Start)],
        CancelOn = [new CancelOn(CancelHandlerFunctions.Stop) { Match = "data.testId" }],
    };

    public override async Task<object?> ExecuteAsync(FunctionContext<RunEvent> context, CancellationToken cancellationToken)
    {
        context.Observed().Record("started");
        await context.Step.SleepAsync("wait", TimeSpan.FromMinutes(5));
        return null;
    }

    public Task OnCancelAsync(FunctionContext<FunctionCancelled> context, CancellationToken cancellationToken)
    {
        CancelHandlerFunctions.Record(context);
        return Task.CompletedTask;
    }
}

internal sealed class CardDeclinedException() : Exception("The card was declined."), IHasErrorCode
{
    public string? Code => "card_declined";
}

// A step and a function that fail with a code, which Inngest keeps with the error.
internal static class ErrorCodeFunctions
{
    public const string Step = "exec/code.step";
    public const string Function = "exec/code.function";

    public static void Add(IInngestBuilder inngest)
    {
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "code-step", Retries = 0, Triggers = [Trigger.Event(Step)] },
            static async (context, _) =>
            {
                try
                {
                    await context.Step.RunAsync<int>("charge", Charge);
                    return null;
                }
                catch (StepFailedException exception)
                {
                    return new CodeSeen(exception.Error.Name, exception.Error.Code);
                }
            });

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "code-function", Retries = 0, Triggers = [Trigger.Event(Function)] },
            static (_, _) =>
            {
                var exception = new NonRetriableException("The account is closed.");
                exception.Data["code"] = "account_closed";
                throw exception;
            },
            onFailure: static (context, _) =>
            {
                var failure = context.Event.Data;
                var testId = failure.Event.GetProperty("data").GetProperty("testId").GetString()!;
                context.Services.GetRequiredService<Probe>().For(testId).Record("on-failure", new CodeSeen(failure.Error.Name, failure.Error.Code));
                return Task.CompletedTask;
            });
    }

    private static int Charge() => throw new CardDeclinedException();
}

// A step whose first result is lost, as when the answer to Inngest never arrives: middleware fails the first attempt
// after the step sent its event, so the step runs and sends again.
internal static class StableIdFunctions
{
    public const string Send = "exec/stable.send";
    public const string Received = "exec/stable.received";
    public const string StepId = "notify";

    public static void Add(IInngestBuilder inngest)
    {
        inngest.AddFunction<RunEvent>(
            new FunctionOptions
            {
                Id = "stable-send",
                Retries = 2,
                Triggers = [Trigger.Event(Send)],
                Middleware = [FunctionMiddleware.Of<LoseFirstResult>()],
            },
            static async (context, _) =>
                (await context.Step.SendEventAsync(StepId, new InngestEvent(Received, new RunEvent(context.Event.Data.TestId)))).Ids.Count);

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "stable-received", Triggers = [Trigger.Event(Received)] },
            static (context, _) =>
            {
                context.Observed().Record("received", context.EventId);
                return Task.FromResult<object?>(null);
            });
    }

    private sealed class LoseFirstResult(Probe probe) : InngestMiddleware
    {
        public override async Task<object?> WrapStepHandlerAsync(FunctionContext context, StepInfo step, Func<Task<object?>> next)
        {
            var output = await next();
            var testId = context.RawEvent.GetProperty("data").GetProperty("testId").GetString()!;
            probe.For(testId).Record("sent", context.Attempt);
            if (context.Attempt == 0)
            {
                throw new InvalidOperationException("The result never reached Inngest.");
            }

            return output;
        }
    }
}

// An invoke from an app that encrypts: the child records the data it reads, and a middleware registered before
// encryption records the field as Inngest sent it.
internal static class EncryptedInvokeFunctions
{
    public const string Parent = "exec/encrypted.parent";
    public const string Secret = "s3cret";

    public static void Add(IInngestBuilder inngest)
    {
        inngest.AddMiddleware<InvokedEventRecorder>();
        inngest.AddEncryption(options => options.Key = "integration-test-key");

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "encrypted-parent", Triggers = [Trigger.Event(Parent)] },
            static async (context, _) => await context.Step.InvokeAsync<string>("child", new InvokeOptions("encrypted-child")
            {
                Data = new { context.Event.Data.TestId, Encrypted = new { Secret } },
            }));

        inngest.AddFunction<JsonElement>(
            new FunctionOptions { Id = "encrypted-child", Triggers = [Trigger.Event("exec/encrypted.child")] },
            static (context, _) =>
            {
                var data = context.Event.Data;
                var testId = data.GetProperty("testId").GetString()!;
                var secret = data.GetProperty("encrypted").GetProperty("secret").GetString();
                context.Services.GetRequiredService<Probe>().For(testId).Record("child-read", secret);
                return Task.FromResult<object?>("ok");
            });
    }

    private sealed class InvokedEventRecorder(Probe probe) : InngestMiddleware, IPayloadTransform
    {
        public bool TransformEvent(JsonElement @event, Utf8JsonWriter writer)
        {
            if (@event.TryGetProperty("name", out var name) && name.ValueEquals(SystemEvents.FunctionInvoked)
                && @event.TryGetProperty("data", out var data) && data.TryGetProperty("testId", out var testId))
            {
                probe.For(testId.GetString()!).Record("sent-as", data.GetProperty("encrypted").GetRawText());
            }

            return false;
        }
    }
}
