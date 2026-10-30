using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Inngest.Encryption;
using Inngest.Tests.Execution;

namespace Inngest.Tests.Encryption;

// Drives the middleware's payload transforms on JSON text the way the engine does: each writes a replacement value or
// writes nothing and keeps the value, in which case these return null.
internal static class EncryptionHarness
{
    public static readonly StepInfo Step = new("a", "hash-of-a", StepType.Run, Memoized: true);

    // The middleware of an app that uses JsVectors.Key unless the callback sets another.
    public static EncryptionMiddleware Middleware(Action<EncryptionOptions>? configure = null) => new(Encryptor(configure));

    public static PayloadEncryptor Encryptor(Action<EncryptionOptions>? configure = null)
    {
        var options = new EncryptionOptions { Key = JsVectors.Key };
        configure?.Invoke(options);
        return new PayloadEncryptor(options, EngineHost.Settings.SerializerOptions);
    }

    public static string? Event(this IPayloadTransform transform, string json) =>
        Write(writer => transform.TransformEvent(JsVectors.Parse(json), writer));

    public static string? Result(this IPayloadTransform transform, string json, StepType type = StepType.Run) =>
        Write(writer => transform.TransformStepResult(null!, Step with { Type = type }, JsVectors.Parse(json), writer));

    public static string? StepOutput(this IPayloadTransform transform, string json) =>
        Write(writer => transform.TransformStepOutput(null!, Step, JsVectors.Parse(json), writer));

    public static string? FunctionOutput(this IPayloadTransform transform, string json) =>
        Write(writer => transform.TransformFunctionOutput(null!, JsVectors.Parse(json), writer));

    public static string DataOf(string envelope) => JsVectors.Parse(envelope).GetProperty("data").GetString()!;

    // The JSON the strategy decrypted from the data of an envelope.
    public static string Decrypt(EncryptionService service, string envelope) => Encoding.UTF8.GetString(service.Decrypt(DataOf(envelope)));

    // Same JSON values, whatever the formatting and the order of the properties.
    public static void ShouldBeJson(this string? actual, string expected)
    {
        actual.ShouldNotBeNull();
        JsonNode.DeepEquals(JsonNode.Parse(actual), JsonNode.Parse(expected)).ShouldBeTrue($"Expected {expected} but was {actual}");
    }

    private static string? Write(Func<Utf8JsonWriter, bool> transform)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        var rewritten = transform(writer);
        writer.Flush();
        if (!rewritten)
        {
            buffer.WrittenCount.ShouldBe(0, "A transform that keeps the value writes nothing.");
            return null;
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
