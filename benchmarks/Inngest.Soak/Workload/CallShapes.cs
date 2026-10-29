using System.Globalization;
using System.Text;
using System.Text.Json;
using Inngest.Execution;

namespace Inngest.Soak.Workload;

// One call of a checkout run as Inngest sends it, and what the SDK must answer: the status, and either the ops
// ("StepRun:ship") or the exact output.
internal sealed record SoakCall(string Shape, byte[] Body, string StepId, int ExpectedStatus, string Expected)
{
    public bool Matches(int status, ReadOnlySpan<byte> body) => status == ExpectedStatus && Describe(status, body) == Expected;

    public static string Describe(int status, ReadOnlySpan<byte> body)
    {
        if (status != 206)
        {
            return Encoding.UTF8.GetString(body);
        }

        var reader = new Utf8JsonReader(body);
        using var document = JsonDocument.ParseValue(ref reader);
        return string.Join(',', document.RootElement.EnumerateArray().Select(static op =>
            $"{op.GetProperty("op").GetString()}:{op.GetProperty("displayName").GetString()}"));
    }
}

internal static class CallShapes
{
    public const int CallsPerRun = 8;

    private const long BaseTimestamp = 1793268000000;

    // The eight calls Inngest makes for one checkout run, in order. Every run has its own run id, order and results.
    public static SoakCall[] ForRun(int run)
    {
        var orderId = $"ord_{run:D6}";
        var totalCents = 1500 + (run * 37 % 9000);
        const int items = 7; // the quantities of the three lines
        var runId = $"01JSOAKRUN{run:D16}";
        var eventJson =
            $$$"""{"data":{"orderId":"{{{orderId}}}","totalCents":{{{totalCents}}},"lines":[{"sku":"TSHIRT-M","quantity":2},{"sku":"MUG","quantity":1},{"sku":"STICKERS","quantity":4}]},"id":"01JSOAKEVT{{{run:D16}}}","name":"{{{Checkout.EventName}}}","ts":{{{BaseTimestamp + run}}},"user":{}}""";

        var reserve = ("reserve-stock", $$$"""{"data":{"orderId":"{{{orderId}}}","items":{{{items}}}}}""");
        var charge = ("charge-card", $$$"""{"data":{"orderId":"{{{orderId}}}","amountCents":{{{totalCents}}},"reference":"ch_{{{orderId}}}_{{{items}}}"}}""");
        var points = ("award-points", $$$"""{"data":{"orderId":"{{{orderId}}}","awarded":{{{totalCents / 100}}}}}""");
        var coolOff = ("cool-off", "null");
        var ship = ("ship", $$$"""{"data":{"orderId":"{{{orderId}}}","trackingNumber":"TRK-{{{orderId}}}"}}""");
        var announce = ("announce", $$$"""{"data":{"ids":["01JSOAKSHIPPED{{{run:D12}}}"]}}""");
        var receipt = JsonSerializer.Serialize(new Receipt(orderId, totalCents, totalCents / 100, $"TRK-{orderId}"), JsonSerializerOptions.Web);

        byte[] Body(params (string Id, string Json)[] completed) => CallBody(runId, eventJson, completed);

        // The parallel steps complete in the other order than they were called, as they often do.
        return
        [
            new("execute-first-step", Body(), "step", 206, "StepRun:reserve-stock"),
            new("report-parallel-steps", Body(reserve), "step", 206, "StepPlanned:charge-card,StepPlanned:award-points"),
            new("execute-requested-step", Body(reserve), StepIds.Hash("charge-card"), 206, "StepRun:charge-card"),
            new("execute-other-requested-step", Body(reserve, charge), StepIds.Hash("award-points"), 206, "StepRun:award-points"),
            new("report-sleep", Body(reserve, points, charge), "step", 206, "Sleep:cool-off"),
            new("execute-step-after-sleep", Body(reserve, points, charge, coolOff), "step", 206, "StepRun:ship"),
            new("send-event-from-step", Body(reserve, points, charge, coolOff, ship), "step", 206, "StepRun:announce"),
            new("return-output", Body(reserve, points, charge, coolOff, ship, announce), "step", 200, receipt),
        ];
    }

    // The body of a call: "steps" keyed by hashed id in sorted order, and the stack in completion order.
    private static byte[] CallBody(string runId, string eventJson, IReadOnlyList<(string Id, string Json)> completed)
    {
        var hashes = completed.Select(static step => StepIds.Hash(step.Id)).ToArray();
        var json = new StringBuilder(1024);
        json.Append(CultureInfo.InvariantCulture,
            $$"""{"ctx":{"attempt":0,"disable_immediate_execution":false,"fn_id":"5a0c4e2b-7d3f-4e8a-9b1c-2f6d8e0a4c7b","max_attempts":4,"run_id":"{{runId}}","stack":{"current":{{hashes.Length}},"stack":[""");
        json.AppendJoin(',', hashes.Select(static hash => $"\"{hash}\""));
        json.Append("]},\"use_api\":false},\"event\":").Append(eventJson).Append(",\"events\":[").Append(eventJson).Append("],\"steps\":{");
        json.AppendJoin(',', completed
            .Select((step, index) => (Hash: hashes[index], step.Json))
            .OrderBy(static step => step.Hash, StringComparer.Ordinal)
            .Select(static step => $"\"{step.Hash}\":{step.Json}"));
        json.Append("},\"use_api\":false,\"version\":2}");
        return Encoding.UTF8.GetBytes(json.ToString());
    }
}
