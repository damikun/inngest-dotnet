using System.Text.Json;
using Inngest.Execution;

namespace Inngest.Tests.Execution;

public sealed class CallContextTests : IDisposable
{
    private readonly EngineHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task The_context_carries_the_request_and_job_ids_of_the_call()
    {
        FunctionContext? seen = null;
        var function = TestFunction.Define(context =>
        {
            seen = context;
            return Task.FromResult<object?>(null);
        });

        using var result = await _host.Engine.ExecuteAsync(
            new ExecutionRequest
            {
                Function = function,
                Body = Call.New().ToBytes(),
                StepId = "step",
                Services = _host.Services,
                RequestId = "req-1",
                JobId = "job-1",
            },
            TestContext.Current.CancellationToken);

        seen.ShouldNotBeNull();
        seen.RequestId.ShouldBe("req-1");
        seen.JobId.ShouldBe("job-1");
    }

    [Fact]
    public async Task Without_ids_from_Inngest_the_context_has_none()
    {
        FunctionContext? seen = null;
        var function = TestFunction.Define(context =>
        {
            seen = context;
            return Task.FromResult<object?>(null);
        });

        await _host.CallAsync(function, Call.New());

        seen!.RequestId.ShouldBeNull();
        seen.JobId.ShouldBeNull();
    }

    [Fact]
    public async Task Code_that_does_not_know_the_event_type_reads_the_event_name_and_id()
    {
        FunctionContext? seen = null;
        var function = TestFunction.Define<Order>(context =>
        {
            seen = context;
            return Task.FromResult<object?>(null);
        });

        await _host.CallAsync(function, Call.New().Event("""{"data":{"total":12},"id":"01JORDER","name":"shop/order.placed","ts":1793268000000}"""));

        seen!.EventName.ShouldBe("shop/order.placed");
        seen.EventId.ShouldBe("01JORDER");
    }

    [Fact]
    public async Task The_raw_event_is_the_event_as_Inngest_sent_it_and_outlives_the_call()
    {
        FunctionContext? seen = null;
        JsonElement raw = default;
        var function = TestFunction.Define<Order>(context =>
        {
            seen = context;
            raw = context.RawEvent;
            return Task.FromResult<object?>(null);
        });

        await _host.CallAsync(function, Call.New().Event("""{"data":{"total":12,"secret":"x"},"id":"01JORDER","name":"shop/order.placed","ts":1793268000000}"""));

        // The request document is gone by now, so these only work on a copy.
        raw.GetProperty("data").GetProperty("secret").GetString().ShouldBe("x");
        seen!.RawEvent.GetProperty("name").GetString().ShouldBe("shop/order.placed");
    }

    [Fact]
    public async Task For_a_batch_the_event_is_the_first_one()
    {
        FunctionContext? seen = null;
        var function = TestFunction.Define(context =>
        {
            seen = context;
            _ = context.RawEvent;
            return Task.FromResult<object?>(null);
        });

        await _host.CallAsync(
            function,
            Call.New().Events(
                """{"data":{"n":1},"id":"01JFIRST","name":"test/event","ts":1793268000000}""",
                """{"data":{"n":2},"id":"01JSECOND","name":"test/event","ts":1793268000001}"""));

        seen!.EventId.ShouldBe("01JFIRST");
        seen.RawEvent.GetProperty("id").GetString().ShouldBe("01JFIRST");
    }

    [Fact]
    public void A_context_built_by_hand_has_no_raw_event()
    {
        var context = new FunctionContext<JsonElement>
        {
            FunctionId = "fn",
            RunId = "run",
            Attempt = 0,
            Step = null!,
            Logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            Services = _host.Services,
            Event = new InngestEvent<JsonElement> { Name = "test/event", Data = default, Id = "01JHAND" },
            Events = [],
        };

        context.EventName.ShouldBe("test/event");
        context.EventId.ShouldBe("01JHAND");
        context.RawEvent.ValueKind.ShouldBe(JsonValueKind.Undefined);
    }

    private sealed record Order(int Total);
}
