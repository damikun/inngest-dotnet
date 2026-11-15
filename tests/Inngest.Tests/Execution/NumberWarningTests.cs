using Inngest.Tests.Checkpointing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Inngest.Tests.Execution;

// Inngest keeps every number as a 64-bit double, so a step's output or an invoke's payload that holds a number a double
// cannot hold as written reaches the next call changed. The SDK says so where it writes the number.
public sealed class NumberWarningTests : IDisposable
{
    private const int StepOutputEventId = 172;
    private const int InvokePayloadEventId = 173;

    private readonly EngineHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task A_step_that_returns_a_64_bit_id_is_logged_with_the_step_and_where_the_number_is()
    {
        var function = TestFunction.Define(async context => await context.Step.RunAsync("ship", () => new Shipment("s1", 1234567890123456789L, 2)));

        var result = await _host.CallAsync(function, Call.New());

        result.OpCodes.ShouldBe(["StepRun"]);
        var warning = Warnings(StepOutputEventId).ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldStartWith("Step ship of run 01JRUN of function fn returned a number at $.trackingId that Inngest does not keep as written");
        warning.Message.ShouldContain("as strings");
    }

    [Fact]
    public async Task A_decimal_with_trailing_zeros_is_logged()
    {
        var function = TestFunction.Define(async context => await context.Step.RunAsync("total", () => 19.90m));

        await _host.CallAsync(function, Call.New());

        Warnings(StepOutputEventId).ShouldHaveSingleItem().Message.ShouldContain("at $ that");
    }

    [Fact]
    public async Task Numbers_that_come_back_the_same_are_not_logged()
    {
        var function = TestFunction.Define(async context =>
            await context.Step.RunAsync("values", () => new Values(int.MaxValue, 1L << 53, 0.1 + 0.2, 12345.6789m, 19.9m, DateTimeOffset.UnixEpoch, "1234567890123456789")));

        await _host.CallAsync(function, Call.New());

        Warnings(StepOutputEventId).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_step_run_under_checkpointing_is_logged_once()
    {
        var function = Checkpointed.Define(async context =>
        {
            var id = await context.Step.RunAsync("load", () => 1234567890123456789L);
            await context.Step.RunAsync("ship", () => id + 1);
            return "done";
        });

        await _host.CallCheckpointedAsync(function, Call.New());

        Warnings(StepOutputEventId).Select(record => record.Message[..10]).ShouldBe(["Step load ", "Step ship "]);
    }

    [Fact]
    public async Task An_invoke_payload_with_a_64_bit_id_is_logged_with_where_the_number_is()
    {
        var function = TestFunction.Define(async context =>
        {
            var safe = context.Step.InvokeAsync<object>("first", new InvokeOptions("child") { Data = new { count = 3 } });
            var risky = context.Step.InvokeAsync<object>("second", new InvokeOptions("child") { Data = new { orderId = 1234567890123456789L } });
            await Task.WhenAll(safe, risky);
            return null;
        });

        var result = await _host.CallAsync(function, Call.New());

        result.OpCodes.ShouldBe(["InvokeFunction", "InvokeFunction"]);
        var warning = Warnings(InvokePayloadEventId).ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldStartWith("Invoke step second of run 01JRUN of function fn sends a number at $.opts.payload.data.orderId");
    }

    private IReadOnlyList<FakeLogRecord> Warnings(int eventId) => [.. _host.Logs.GetSnapshot().Where(record => record.Id.Id == eventId)];

    public sealed record Shipment(string Id, long TrackingId, int Parcels);

    public sealed record Values(int Int, long Long, double Double, decimal Decimal, decimal Price, DateTimeOffset Time, string Id);
}
