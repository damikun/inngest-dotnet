using System.Text;
using Inngest.Execution;

namespace Inngest.Benchmarks.Support;

// A result Inngest has stored for a step: Json is the value under "steps", such as {"data":{...}}.
internal readonly record struct MemoizedStep(string Id, string Json);

// Builds the body of a call from Inngest the way the server sends it: "steps" keyed by hashed step id in sorted
// order, and the stack listing the ids in the order the steps completed.
internal static class CallBody
{
    public const string RunId = "01JC8Y3P8E0F1G2H3J4K5M6N7P";

    public static byte[] Create(IReadOnlyList<MemoizedStep> completionOrder, bool disableImmediateExecution = false)
    {
        var hashes = completionOrder.Select(static step => StepIds.Hash(step.Id)).ToArray();
        var eventJson = Samples.EventJson();

        var json = new StringBuilder();
        json.Append(
            $$"""{"ctx":{"attempt":0,"disable_immediate_execution":{{(disableImmediateExecution ? "true" : "false")}},"fn_id":"7f9c2e1a-4b6d-4c8e-9a3f-0d5b1e2c3a4f","max_attempts":4,"run_id":"{{RunId}}","stack":{"current":{{hashes.Length}},"stack":[""");
        json.AppendJoin(',', hashes.Select(static hash => $"\"{hash}\""));
        json.Append("]},\"use_api\":false},\"event\":").Append(eventJson).Append(",\"events\":[").Append(eventJson).Append("],\"steps\":{");
        json.AppendJoin(
            ',',
            completionOrder
                .Select((step, index) => (Hash: hashes[index], step.Json))
                .OrderBy(static step => step.Hash, StringComparer.Ordinal)
                .Select(static step => $"\"{step.Hash}\":{step.Json}"));
        json.Append("},\"use_api\":false,\"version\":2}");
        return Encoding.UTF8.GetBytes(json.ToString());
    }

    // Steps that completed one after another, as in a function that awaits each step before starting the next.
    public static byte[] Sequential(IReadOnlyList<string> stepIds) =>
        Create([.. stepIds.Select(static (id, index) => new MemoizedStep(id, Samples.MemoizedResultJson(index)))]);

    // Steps that ran in parallel completed in no particular order, so the stack is a fixed shuffle of the call order.
    public static byte[] Parallel(IReadOnlyList<string> stepIds)
    {
        var steps = stepIds.Select(static (id, index) => new MemoizedStep(id, Samples.MemoizedResultJson(index))).ToArray();
        new Random(2026).Shuffle(steps);
        return Create(steps);
    }

    // A first call: nothing has been memoized yet.
    public static byte[] First() => Create([]);
}
