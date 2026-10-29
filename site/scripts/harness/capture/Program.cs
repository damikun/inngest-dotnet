using System.Text;
using System.Text.Json.Nodes;
using Inngest;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Time.Testing;
using Replay;

// Runs FulfillOrder in-process and calls it the way Inngest's executor does, from the first call to the final
// answer, and writes every request and the SDK's real response to the file given as the first argument.

var output = args[0];
var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 9, 30, 0, TimeSpan.Zero));

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
builder.WebHost.UseTestServer();
builder.Configuration.Sources.Clear();
builder.Logging.ClearProviders();
builder.Services.AddSingleton<TimeProvider>(time);
builder.Services.AddSingleton(time);
builder.Services.AddSingleton<FakeServices>();
builder.Services.AddSingleton<IInventory>(provider => provider.GetRequiredService<FakeServices>());
builder.Services.AddSingleton<IPayments>(provider => provider.GetRequiredService<FakeServices>());
builder.Services.AddSingleton<IMailer>(provider => provider.GetRequiredService<FakeServices>());
builder.Services.AddSingleton<IWarehouse>(provider => provider.GetRequiredService<FakeServices>());
builder.Services
    .AddInngest(options =>
    {
        options.AppId = "shop";
        options.IsDev = true;
    })
    .AddFunction<FulfillOrder>(new FunctionOptions
    {
        Id = "fulfill-order",
        Triggers = [Trigger.Event("shop/order.placed")],
        Retries = 3,
    });

await using var app = builder.Build();
app.MapInngest("/api/inngest");
await app.StartAsync();
var client = app.GetTestClient();

const string FunctionId = "shop-fulfill-order";
const string RunId = "01JDZ6Q3T8K3V2M4H5N6P7R8S9";
var placedAt = time.GetUtcNow().AddSeconds(-2).ToUnixTimeMilliseconds();
var @event = new JsonObject
{
    ["name"] = "shop/order.placed",
    ["data"] = new JsonObject { ["orderId"] = "ord_1042", ["customerId"] = "cus_7d2e", ["total"] = 59.90m },
    ["id"] = "01JDZ6Q2XF4H9K1M3N5P7R9T2V",
    ["ts"] = placedAt,
    ["user"] = new JsonObject(),
};

var steps = new JsonObject();
var stack = new JsonArray();
var names = new JsonObject();
var calls = new JsonArray();

while (true)
{
    var ops = await CallAsync("step", disableImmediateExecution: false);
    if (ops is null)
    {
        break;
    }

    if (ops.Count == 1 && (string?)ops[0]!["op"] is "StepRun")
    {
        Memoize(ops[0]!, new JsonObject { ["data"] = ops[0]!["data"]?.DeepClone() });
        continue;
    }

    if (ops.Count == 1 && (string?)ops[0]!["op"] is "Sleep")
    {
        // Inngest wakes the run once the sleep is over and memoizes it as null.
        time.Advance(TimeSpan.FromMinutes(30));
        Memoize(ops[0]!, null);
        continue;
    }

    // Planned steps run in calls of their own, which Inngest makes at the same time with the same state.
    var results = new List<JsonNode>();
    foreach (var planned in ops)
    {
        var result = await CallAsync((string)planned!["id"]!, disableImmediateExecution: true)
            ?? throw new InvalidOperationException("A planned step answered 200.");
        results.Add(result[0]!);
    }

    foreach (var result in results)
    {
        Memoize(result, new JsonObject { ["data"] = result["data"]?.DeepClone() });
    }
}

await File.WriteAllTextAsync(output, new JsonObject { ["stepNames"] = names, ["calls"] = calls }.ToJsonString(new() { WriteIndented = true }));
return;

void Memoize(JsonNode op, JsonNode? value)
{
    var id = (string)op["id"]!;
    steps[id] = value;
    stack.Add(id);
}

// Returns the ops of a 206 answer, or null once the function has returned.
async Task<JsonArray?> CallAsync(string stepId, bool disableImmediateExecution)
{
    var body = new JsonObject
    {
        ["event"] = @event.DeepClone(),
        ["events"] = new JsonArray(@event.DeepClone()),
        ["steps"] = steps.DeepClone(),
        ["ctx"] = new JsonObject
        {
            ["run_id"] = RunId,
            ["attempt"] = 0,
            ["max_attempts"] = 4,
            ["stack"] = new JsonObject { ["stack"] = stack.DeepClone(), ["current"] = stack.Count },
            ["disable_immediate_execution"] = disableImmediateExecution,
            ["use_api"] = false,
        },
        ["version"] = 2,
    };

    var url = $"/api/inngest?fnId={FunctionId}&stepId={stepId}";
    using var response = await client.PostAsync(url, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
    var text = await response.Content.ReadAsStringAsync();
    var answer = JsonNode.Parse(text);
    var headers = new JsonObject();
    foreach (var name in new[] { "X-Inngest-SDK", "X-Inngest-Req-Version", "X-Inngest-No-Retry" })
    {
        if (response.Headers.TryGetValues(name, out var values))
        {
            headers[name] = values.First();
        }
    }

    calls.Add(new JsonObject
    {
        ["request"] = new JsonObject { ["method"] = "POST", ["url"] = url, ["body"] = body },
        ["response"] = new JsonObject { ["status"] = (int)response.StatusCode, ["headers"] = headers, ["body"] = answer?.DeepClone() },
    });

    if ((int)response.StatusCode == 200)
    {
        return null;
    }

    if ((int)response.StatusCode != 206 || answer is not JsonArray ops)
    {
        throw new InvalidOperationException($"Unexpected answer {(int)response.StatusCode}: {text}");
    }

    foreach (var op in ops)
    {
        names[(string)op!["id"]!] = (string?)op["displayName"];
    }

    return ops;
}
