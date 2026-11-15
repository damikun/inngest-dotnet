using Inngest.Execution;
using Inngest.Tests.Checkpointing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Inngest.Tests.Execution;

// Inngest keeps every number as a 64-bit double, so a step's output or an invoke's payload that holds a number a double
// cannot hold as written reaches the next call changed. The SDK says so where it writes the number: as a warning when
// the value changes, and at debug level when only the trailing zeros of a decimal go.
public sealed class NumberWarningTests : IDisposable
{
    private const int StepOutputEventId = 172;
    private const int InvokePayloadEventId = 173;
    private const int StepOutputTrailingZerosEventId = 174;
    private const int InvokePayloadTrailingZerosEventId = 175;

    private readonly EngineHost _host = new(services => services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Debug)));

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task A_step_that_returns_a_64_bit_id_is_logged_with_the_step_and_where_the_number_is()
    {
        var function = TestFunction.Define(async context => await context.Step.RunAsync("ship", () => new Shipment("s1", 1234567890123456789L, 2)));

        var result = await _host.CallAsync(function, Call.New());

        result.OpCodes.ShouldBe(["StepRun"]);
        var warning = Logged(StepOutputEventId).ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldStartWith("Step ship of run 01JRUN of function fn returned a number at $.trackingId that Inngest does not keep as written");
        warning.Message.ShouldContain("as strings");
    }

    [Fact]
    public async Task A_decimal_that_only_loses_its_trailing_zeros_is_logged_at_debug_level()
    {
        var function = TestFunction.Define(async context => await context.Step.RunAsync("total", () => 19.90m));

        await _host.CallAsync(function, Call.New());

        Logged(StepOutputEventId).ShouldBeEmpty();
        var record = Logged(StepOutputTrailingZerosEventId).ShouldHaveSingleItem();
        record.Level.ShouldBe(LogLevel.Debug);
        record.Message.ShouldStartWith("Step total of run 01JRUN of function fn returned a number at $ that comes back without its trailing zeros");
    }

    [Fact]
    public async Task A_number_whose_value_changes_is_logged_as_a_warning_even_after_trailing_zeros()
    {
        var function = TestFunction.Define(async context =>
            await context.Step.RunAsync("ship", () => new { price = 19.90m, trackingId = 1234567890123456789L }));

        await _host.CallAsync(function, Call.New());

        Logged(StepOutputEventId).ShouldHaveSingleItem().Message.ShouldContain("at $.trackingId that");
        Logged(StepOutputTrailingZerosEventId).ShouldBeEmpty();
    }

    [Fact]
    public async Task Numbers_that_come_back_the_same_are_not_logged()
    {
        var function = TestFunction.Define(async context =>
            await context.Step.RunAsync("values", () => new Values(int.MaxValue, 1L << 53, 0.1 + 0.2, 12345.6789m, 19.9m, DateTimeOffset.UnixEpoch, "1234567890123456789")));

        await _host.CallAsync(function, Call.New());

        Logged(StepOutputEventId).ShouldBeEmpty();
        Logged(StepOutputTrailingZerosEventId).ShouldBeEmpty();
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

        Logged(StepOutputEventId).Select(record => record.Message[..10]).ShouldBe(["Step load ", "Step ship "]);
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
        var warning = Logged(InvokePayloadEventId).ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldStartWith("Invoke step second of run 01JRUN of function fn sends a number at $.opts.payload.data.orderId");
    }

    // A race calls the function after each of its steps, and the function reports an invoke that is still pending again.
    [Fact]
    public async Task An_invoke_reported_again_by_a_later_call_of_the_run_is_logged_once()
    {
        var function = TestFunction.Define(async context =>
        {
            await context.Step.ParallelAsync(ParallelMode.Race, () => Task.WhenAny(
                context.Step.InvokeAsync<object>("charge", new InvokeOptions("child") { Data = new { orderId = 1234567890123456789L } }),
                context.Step.RunAsync("ship", () => 1)));
            return null;
        });

        var first = await _host.CallAsync(function, Call.New());
        var second = await _host.CallAsync(function, Call.New().Data("ship", "1").DisableImmediateExecution());

        first.OpCodes.ShouldBe(["InvokeFunction", "StepPlanned"]);
        second.OpCodes.ShouldBe(["InvokeFunction"]);
        Logged(InvokePayloadEventId).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Each_pending_invoke_of_a_race_is_logged_once()
    {
        var function = TestFunction.Define(async context =>
        {
            await context.Step.ParallelAsync(ParallelMode.Race, () => Task.WhenAny(
                context.Step.InvokeAsync<object>("charge", new InvokeOptions("child") { Data = new { orderId = 1234567890123456789L } }),
                context.Step.InvokeAsync<object>("refund", new InvokeOptions("child") { Data = new { orderId = 1234567890123456789L } }),
                context.Step.RunAsync("ship", () => 1)));
            return null;
        });

        var first = await _host.CallAsync(function, Call.New());
        var second = await _host.CallAsync(function, Call.New().Data("ship", "1").DisableImmediateExecution());

        first.OpCodes.ShouldBe(["InvokeFunction", "InvokeFunction", "StepPlanned"]);
        second.OpCodes.ShouldBe(["InvokeFunction", "InvokeFunction"]);
        Logged(InvokePayloadEventId).Select(record => record.Message.Split(" sends ")[0]).Order().ShouldBe(
            ["Invoke step charge of run 01JRUN of function fn", "Invoke step refund of run 01JRUN of function fn"]);
    }

    [Fact]
    public async Task The_same_invoke_in_another_run_is_logged_again()
    {
        var function = TestFunction.Define(async context =>
            await context.Step.InvokeAsync<object>("charge", new InvokeOptions("child") { Data = new { orderId = 1234567890123456789L } }));

        await _host.CallAsync(function, Call.New());
        await _host.CallAsync(function, Call.New().RunId("01JOTHERRUN"));

        Logged(InvokePayloadEventId).Select(record => record.Message.Split(" sends ")[0]).ShouldBe(
            ["Invoke step charge of run 01JRUN of function fn", "Invoke step charge of run 01JOTHERRUN of function fn"]);
    }

    [Fact]
    public async Task An_invoke_payload_that_only_loses_trailing_zeros_is_logged_at_debug_level()
    {
        var function = TestFunction.Define(async context =>
            await context.Step.InvokeAsync<object>("charge", new InvokeOptions("child") { Data = new { amount = 10.00m } }));

        await _host.CallAsync(function, Call.New());

        Logged(InvokePayloadEventId).ShouldBeEmpty();
        var record = Logged(InvokePayloadTrailingZerosEventId).ShouldHaveSingleItem();
        record.Level.ShouldBe(LogLevel.Debug);
        record.Message.ShouldStartWith("Invoke step charge of run 01JRUN of function fn sends a number at $.opts.payload.data.amount that comes back without its trailing zeros");
    }

    [Fact]
    public async Task An_invoke_from_a_durable_endpoint_is_logged_too()
    {
        var endpoint = TestFunction.Define(
            async context =>
            {
                await context.Step.InvokeAsync<object>("charge", new InvokeOptions("child") { Data = new { amount = 0.1234567890123456789m } });
                return null;
            },
            "POST /orders");

        var outcome = await _host.EndpointAsync(endpoint);

        outcome.Kind.ShouldBe(EndpointOutcomeKind.HandedOver);
        _host.EndpointCheckpoints.Saves.ShouldHaveSingleItem().OpNames.ShouldBe(["InvokeFunction"]);
        Logged(InvokePayloadEventId).ShouldHaveSingleItem().Message
            .ShouldStartWith("Invoke step charge of run 01JENDPOINTRUN00000000000000 of function POST /orders sends a number at $.opts.payload.data.amount");
    }

    private IReadOnlyList<FakeLogRecord> Logged(int eventId) => [.. _host.Logs.GetSnapshot().Where(record => record.Id.Id == eventId)];

    public sealed record Shipment(string Id, long TrackingId, int Parcels);

    public sealed record Values(int Int, long Long, double Double, decimal Decimal, decimal Price, DateTimeOffset Time, string Id);
}
