using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.Core;

internal sealed record ReportEvent(string TestId, bool Fail) : IScenarioEvent;

internal sealed record ReportResult(int Rows);

internal sealed record SecretNote(string TestId, string Encrypted) : IScenarioEvent;

// What a cleanup function saw: the typed fields and the event data exactly as the server sent it.
internal sealed record SystemEventSeen(string FunctionId, string RunId, string RawData);

internal sealed record FinishedSeen(string RunId, string? Result, FunctionError? Error);

internal sealed record CaughtFailure(string Name, string Message, string? CauseType, string? CauseName, string? CauseMessage);

internal static class CoreFunctions
{
    public const string AppId = "core";

    public const string Import = "core/import";
    public const string ImportStop = "core/import.stop";
    public const string Report = "core/report";
    public const string Charge = "core/charge";
    public const string ChargeRethrow = "core/charge.rethrow";
    public const string Sealed = "core/sealed";
    public const string SealedNote = "core/sealed.note";

    public const string JitterId = "jitter";
    public static readonly TimeSpan Jitter = TimeSpan.FromMinutes(5);

    public static void Add(IInngestBuilder inngest)
    {
        AddCleanup(inngest);
        AddErrors(inngest);
        AddSealed(inngest);

        // Once a year, so it never fires during a test; only the sync matters.
        inngest.AddFunction<JsonElement>(
            new FunctionOptions { Id = JitterId, Triggers = [Trigger.Cron("0 0 1 1 *", Jitter)] },
            static (_, _) => Task.FromResult<object?>(null));
    }

    private static void AddCleanup(IInngestBuilder inngest)
    {
        inngest.AddFunction<RunEvent>(
            new FunctionOptions
            {
                Id = "import",
                Triggers = [Trigger.Event(Import)],
                CancelOn = [new CancelOn(ImportStop) { Match = "data.testId" }],
            },
            static async (context, _) =>
            {
                await context.Step.SleepAsync("wait", TimeSpan.FromMinutes(10));
                return "imported";
            });

        inngest.AddFunction<FunctionCancelled>(
            new FunctionOptions
            {
                Id = "import-cancelled",
                Triggers = [Trigger.Event(SystemEvents.FunctionCancelled, $"event.data.function_id == '{AppId}-import'")],
            },
            static (context, _) =>
            {
                var data = context.Event.Data;
                Observed(context, data.Event).Record("cancelled", new SystemEventSeen(data.FunctionId, data.RunId, RawData(context)));
                return Task.FromResult<object?>(null);
            });

        inngest.AddFunction<ReportEvent>(
            new FunctionOptions { Id = "report", Retries = 0, Triggers = [Trigger.Event(Report)] },
            static (context, _) => context.Event.Data.Fail
                ? throw new NonRetriableException("report failed", new InvalidDataException("row 3 is not a number"))
                : Task.FromResult<object?>(new ReportResult(3)));

        inngest.AddFunction<FunctionFinished>(
            new FunctionOptions
            {
                Id = "report-finished",
                Triggers = [Trigger.Event(SystemEvents.FunctionFinished, $"event.data.function_id == '{AppId}-report'")],
            },
            static (context, _) =>
            {
                var data = context.Event.Data;
                var result = data.Result.ValueKind == JsonValueKind.Undefined ? null : data.Result.GetRawText();
                var observed = Observed(context, data.Event);
                observed.Record("finished", new FinishedSeen(data.RunId, result, data.Error));
                observed.Record("finished-raw", RawData(context));
                return Task.FromResult<object?>(null);
            });
    }

    private static void AddErrors(IInngestBuilder inngest)
    {
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "charge", Retries = 0, Triggers = [Trigger.Event(Charge)] },
            static async (context, _) =>
            {
                try
                {
                    return await context.Step.RunAsync<int>("charge", _ => throw Declined());
                }
                catch (StepFailedException failure)
                {
                    var cause = failure.InnerException as FunctionErrorException;
                    return new CaughtFailure(failure.Error.Name, failure.Message, cause?.GetType().Name, cause?.Error.Name, cause?.Message);
                }
            });

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "charge-rethrow", Retries = 0, Triggers = [Trigger.Event(ChargeRethrow)] },
            static async (context, _) => await context.Step.RunAsync<int>("charge", _ => throw Declined()));
    }

    // The sender seals its step output, its output and the field "encrypted" of the event it sends; the receiver opens
    // that field. Both use SealingMiddleware as function middleware.
    private static void AddSealed(IInngestBuilder inngest)
    {
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "sealed", Triggers = [Trigger.Event(Sealed)], Middleware = [FunctionMiddleware.Of<SealingMiddleware>()] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                var secret = await context.Step.RunAsync("secret", () => "s3cret value");
                observed.Record("secret", secret);
                await context.Step.SendEventAsync("notify", new InngestEvent(SealedNote, new SecretNote(context.Event.Data.TestId, secret)));
                return new SecretNote(context.Event.Data.TestId, secret);
            });

        inngest.AddFunction<SecretNote>(
            new FunctionOptions { Id = "sealed-receiver", Triggers = [Trigger.Event(SealedNote)], Middleware = [FunctionMiddleware.Of<SealingMiddleware>()] },
            static (context, _) =>
            {
                context.Observed().Record("note", context.Event.Data.Encrypted);
                return Task.FromResult<object?>(null);
            });
    }

    private static InvalidOperationException Declined() =>
        new("declined", new TimeoutException("gateway timed out"));

    // A system event carries the event that triggered the run it reports on, and with it the scenario's test id.
    private static TestRecording Observed(FunctionContext context, JsonElement triggering) =>
        context.Services.GetRequiredService<Probe>().For(triggering.GetProperty("data").GetProperty("testId").GetString()!);

    private static string RawData(FunctionContext context) => context.RawEvent.GetProperty("data").GetRawText();
}

// A reversible stand-in for encryption, in the envelope of the TypeScript SDK's encryption middleware: base64 of the
// JSON. It seals step output, function output and the "encrypted" field of sent events, and opens them on the way in.
internal sealed class SealingMiddleware(Probe probe) : InngestMiddleware, IPayloadTransform
{
    private const string Field = "encrypted";
    private const string Marker = "__ENCRYPTED__";

    public bool TransformEvent(JsonElement @event, Utf8JsonWriter writer)
    {
        if (!@event.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty(Field, out var field) || !IsSealed(field))
        {
            return false;
        }

        probe.For(data.GetProperty("testId").GetString()!).Record("received", field.GetRawText());
        writer.WriteStartObject();
        foreach (var property in @event.EnumerateObject())
        {
            if (!property.NameEquals("data"))
            {
                property.WriteTo(writer);
                continue;
            }

            writer.WriteStartObject("data");
            foreach (var item in data.EnumerateObject())
            {
                if (item.NameEquals(Field))
                {
                    writer.WritePropertyName(Field);
                    writer.WriteRawValue(Open(item.Value));
                }
                else
                {
                    item.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        return true;
    }

    public bool TransformStepResult(FunctionContext context, StepInfo step, JsonElement result, Utf8JsonWriter writer)
    {
        if (!IsSealed(result))
        {
            return false;
        }

        probe.For(context.RawEvent.GetProperty("data").GetProperty("testId").GetString()!).Record("memoized", $"{step.Id}={result.GetRawText()}");
        writer.WriteRawValue(Open(result));
        return true;
    }

    public bool TransformStepOutput(FunctionContext context, StepInfo step, JsonElement output, Utf8JsonWriter writer) => Seal(output, writer);

    public bool TransformFunctionOutput(FunctionContext context, JsonElement output, Utf8JsonWriter writer) => Seal(output, writer);

    public override Task<SendEventsResult> WrapSendEventAsync(
        FunctionContext? context,
        IReadOnlyList<InngestEvent> events,
        Func<IReadOnlyList<InngestEvent>, Task<SendEventsResult>> next,
        CancellationToken cancellationToken) =>
        next([.. events.Select(SealField)]);

    public static string SealJson(string json) =>
        $$"""{"{{Marker}}":true,"__STRATEGY__":"test/base64","data":"{{Convert.ToBase64String(Encoding.UTF8.GetBytes(json))}}"}""";

    public static byte[] Open(JsonElement envelope) => envelope.GetProperty("data").GetBytesFromBase64();

    public static bool IsSealed(JsonElement value) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(Marker, out _);

    private static InngestEvent SealField(InngestEvent @event)
    {
        if (@event.Data is null || JsonSerializer.SerializeToNode(@event.Data, @event.Data.GetType(), JsonSerializerOptions.Web) is not JsonObject data
            || data[Field] is not { } field)
        {
            return @event;
        }

        data[Field] = JsonNode.Parse(SealJson(field.ToJsonString()));
        return @event with { Data = data };
    }

    // Null has nothing to hide, as in the TypeScript SDK.
    private static bool Seal(JsonElement value, Utf8JsonWriter writer)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        writer.WriteRawValue(SealJson(value.GetRawText()));
        return true;
    }
}
