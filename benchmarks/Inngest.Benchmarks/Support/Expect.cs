using System.Text;
using System.Text.Json;
using Inngest.Execution;

namespace Inngest.Benchmarks.Support;

// Setup checks. A benchmark that measures the wrong thing is worse than none, so a surprise stops the run.
internal static class Expect
{
    public static void That(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Benchmark check failed: {what}.");
        }
    }

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Benchmark check failed: {what} was {actual}, expected {expected}.");
        }
    }

    // The engine's answer as JSON, after checking the status. A failure carries the body, because the engine
    // explains itself there.
    public static JsonDocument Answer(ExecutionResult result, int status)
    {
        if (result.StatusCode != status)
        {
            var body = Encoding.UTF8.GetString(result.Body.WrittenSpan);
            throw new InvalidOperationException($"Benchmark check failed: the engine answered {result.StatusCode} {body}, expected {status}.");
        }

        return JsonDocument.Parse(result.Body.WrittenMemory.ToArray());
    }

    public static JsonElement Property(JsonElement element, string name)
    {
        That(element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out _), $"the JSON has a \"{name}\" property");
        return element.GetProperty(name);
    }
}
