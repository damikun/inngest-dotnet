using System.Net;
using Inngest.Tests.Client;
using Inngest.Tests.Execution;
using Inngest.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Tests.Events;

// Every overload that takes a typed event takes the event's name, and its version when the type has one, from the
// type, and ends up in the same place as the string form.
public sealed class TypedEventTests : IDisposable
{
    private static readonly StepOptions Named = new("step-id") { Name = "A name" };

    private readonly EngineHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task The_client_sends_typed_data_under_the_name_of_its_type()
    {
        var fixture = new ClientFixture();
        fixture.Handler.Respond(HttpStatusCode.OK, ClientFixture.Accepted);

        var result = await fixture.Client.SendAsync(new OrderPaid("o_1", 12.5m));

        result.Ids.ShouldBe(["01HQ0000000000000000000001"]);
        fixture.Handler.Single.BodyText.ShouldBe(
            """[{"name":"shop/order.paid","data":{"orderId":"o_1","amount":12.5},"ts":1705586504000}]""");
    }

    [Fact]
    public async Task The_client_sends_the_version_the_type_declares()
    {
        var fixture = new ClientFixture();
        fixture.Handler.Respond(HttpStatusCode.OK, ClientFixture.Accepted);

        await fixture.Client.SendAsync(new OrderRefunded("o_1"));

        fixture.Handler.Single.BodyText.ShouldBe(
            """[{"name":"shop/order.refunded","data":{"orderId":"o_1"},"ts":1705586504000,"v":"2"}]""");
    }

    [Fact]
    public async Task The_client_sends_several_typed_events_in_one_request()
    {
        var fixture = new ClientFixture();
        fixture.Handler.Respond(HttpStatusCode.OK, """{"ids":["a","b"],"status":200}""");

        var result = await fixture.Client.SendAsync([new OrderPaid("o_1", 1m), new OrderPaid("o_2", 2m)]);

        result.Ids.ShouldBe(["a", "b"]);
        fixture.Handler.Single.BodyText.ShouldBe(
            """[{"name":"shop/order.paid","data":{"orderId":"o_1","amount":1},"ts":1705586504000},{"name":"shop/order.paid","data":{"orderId":"o_2","amount":2},"ts":1705586504000}]""");
    }

    [Fact]
    public async Task The_client_rejects_null_typed_data_before_sending()
    {
        var fixture = new ClientFixture();

        await Should.ThrowAsync<ArgumentNullException>(() => fixture.Client.SendAsync((OrderPaid)null!));
        await Should.ThrowAsync<ArgumentNullException>(() => fixture.Client.SendAsync((IEnumerable<OrderPaid>)null!));
        await Should.ThrowAsync<ArgumentNullException>(() => fixture.Client.SendAsync([new OrderPaid("o_1", 1m), null!]));

        fixture.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Any_client_receives_the_typed_data_as_an_inngest_event()
    {
        var client = new RecordingClient();

        await client.SendAsync(new OrderRefunded("o_1"));
        await client.SendAsync([new OrderPaid("o_2", 2m)]);

        client.Sent.ShouldBe(
        [
            new InngestEvent("shop/order.refunded", new OrderRefunded("o_1")) { Version = "2" },
            new InngestEvent("shop/order.paid", new OrderPaid("o_2", 2m)),
        ]);
    }

    [Fact]
    public void A_trigger_for_a_type_uses_the_name_of_the_type()
    {
        Trigger.Event<OrderPaid>().ShouldBe(new EventTrigger("shop/order.paid"));
        Trigger.Event<OrderPaid>("event.data.amount > 100").ShouldBe(new EventTrigger("shop/order.paid") { If = "event.data.amount > 100" });
    }

    [Fact]
    public void A_cancellation_for_a_type_uses_the_name_of_the_type()
    {
        CancelOn.For<OrderRefunded>().ShouldBe(new CancelOn("shop/order.refunded"));
        (CancelOn.For<OrderRefunded>() with { Match = "data.orderId", Timeout = TimeSpan.FromHours(1) })
            .ShouldBe(new CancelOn("shop/order.refunded") { Match = "data.orderId", Timeout = TimeSpan.FromHours(1) });
    }

    [Fact]
    public async Task A_typed_wait_reaches_the_step_tools_with_the_name_of_the_type()
    {
        var fake = new RecordingStepTools();
        IStepTools step = fake;

        await step.WaitForEventAsync<OrderRefunded>("wait", TimeSpan.FromHours(1), match: "data.orderId", @if: "async.data.full");
        await step.WaitForEventAsync<OrderRefunded>(Named, TimeSpan.FromMinutes(5));

        fake.Waits.ShouldBe(
        [
            ("wait", new WaitForEventOptions("shop/order.refunded", TimeSpan.FromHours(1)) { Match = "data.orderId", If = "async.data.full" }),
            ("step-id", new WaitForEventOptions("shop/order.refunded", TimeSpan.FromMinutes(5))),
        ]);
    }

    [Fact]
    public async Task A_typed_wait_reports_the_event_of_the_type_to_inngest()
    {
        var function = TestFunction.Define(async context =>
        {
            await context.Step.WaitForEventAsync<OrderRefunded>(Named, TimeSpan.FromHours(1), match: "data.orderId");
            return null;
        });

        var result = await _host.CallAsync(function, Call.New());

        var op = result.Op();
        op.GetProperty("op").GetString().ShouldBe("WaitForEvent");
        op.GetProperty("displayName").GetString().ShouldBe("A name");
        var opts = op.GetProperty("opts");
        opts.GetProperty("event").GetString().ShouldBe("shop/order.refunded");
        opts.GetProperty("timeout").GetString().ShouldBe("1h");
        opts.GetProperty("if").GetString().ShouldBe("event.data.orderId == async.data.orderId");
    }

    [Fact]
    public async Task A_typed_wait_returns_the_event_it_received_with_typed_data()
    {
        var function = TestFunction.Define(async context =>
        {
            var refund = await context.Step.WaitForEventAsync<OrderRefunded>("wait", TimeSpan.FromHours(1));
            return refund is null ? "timed out" : $"{refund.Name} {refund.Data.OrderId} v{refund.Version}";
        });

        var result = await _host.CallAsync(function, Call.New().Memo(
            "wait", """{"name":"shop/order.refunded","data":{"orderId":"o_9"},"id":"01JREFUND","ts":1793268000000,"v":"2"}"""));

        result.Body.ShouldBe("\"shop/order.refunded o_9 v2\"");
    }

    [Fact]
    public async Task Typed_send_steps_reach_the_step_tools_as_inngest_events()
    {
        var fake = new RecordingStepTools();
        IStepTools step = fake;

        await step.SendEventAsync("one", new OrderPaid("o_1", 1m));
        await step.SendEventAsync("many", [new OrderPaid("o_2", 2m), new OrderPaid("o_3", 3m)]);
        await step.SendEventAsync(Named, new OrderRefunded("o_4"));
        await step.SendEventAsync(Named, [new OrderRefunded("o_5")]);

        fake.Sends.Select(send => send.Id).ShouldBe(["one", "many", "step-id", "step-id"]);
        fake.Sends.SelectMany(send => send.Events).ShouldBe(
        [
            new InngestEvent("shop/order.paid", new OrderPaid("o_1", 1m)),
            new InngestEvent("shop/order.paid", new OrderPaid("o_2", 2m)),
            new InngestEvent("shop/order.paid", new OrderPaid("o_3", 3m)),
            new InngestEvent("shop/order.refunded", new OrderRefunded("o_4")) { Version = "2" },
            new InngestEvent("shop/order.refunded", new OrderRefunded("o_5")) { Version = "2" },
        ]);
    }

    [Fact]
    public async Task Typed_send_steps_reject_null_data()
    {
        IStepTools step = new RecordingStepTools();

        await Should.ThrowAsync<ArgumentNullException>(() => step.SendEventAsync("one", (OrderPaid)null!));
        await Should.ThrowAsync<ArgumentNullException>(() => step.SendEventAsync("many", (IEnumerable<OrderPaid>)null!));
        await Should.ThrowAsync<ArgumentNullException>(() => step.SendEventAsync(Named, (IEnumerable<OrderPaid>)null!));
    }

    [Fact]
    public async Task A_typed_send_step_sends_the_event_from_a_run()
    {
        var services = new ServiceCollection();
        services.AddInngest(options => options.AppId = "shop")
            .AddFunction(new FunctionOptions { Id = "refund", Triggers = [Trigger.Event<OrderPaid>()] }, async (context, _) =>
            {
                await context.Step.SendEventAsync("refund", new OrderRefunded(context.Event.Data.GetProperty("orderId").GetString()!));
                return null;
            });
        await using var host = new InngestTestHost(services);

        var run = await host.RunAsync("refund", new InngestEvent(OrderPaid.EventName, new OrderPaid("o_1", 1m)), TestContext.Current.CancellationToken);

        run.Status.ShouldBe(TestRunStatus.Completed);
        var sent = run.SentEvents.ShouldHaveSingleItem();
        sent.Name.ShouldBe("shop/order.refunded");
        sent.Version.ShouldBe("2");
        sent.Data.ShouldBe(new OrderRefunded("o_1"));
    }

    private sealed class RecordingClient : IInngestClient
    {
        public List<InngestEvent> Sent { get; } = [];

        public string AppId => "shop";

        public Task<SendEventsResult> SendAsync(InngestEvent @event, CancellationToken cancellationToken = default) =>
            SendAsync([@event], cancellationToken);

        public Task<SendEventsResult> SendAsync(IEnumerable<InngestEvent> events, CancellationToken cancellationToken = default)
        {
            var list = events.ToList();
            Sent.AddRange(list);
            return Task.FromResult(new SendEventsResult([.. list.Select((_, i) => $"id-{i}")]));
        }
    }

    // Implements only the original members, as a hand-written fake does.
    private sealed class RecordingStepTools : IStepTools
    {
        public List<(string Id, WaitForEventOptions Options)> Waits { get; } = [];

        public List<(string Id, IReadOnlyList<InngestEvent> Events)> Sends { get; } = [];

        public Task<T> RunAsync<T>(string id, Func<CancellationToken, Task<T>> handler) => handler(CancellationToken.None);

        public Task SleepAsync(string id, TimeSpan duration) => Task.CompletedTask;

        public Task SleepUntilAsync(string id, DateTimeOffset until) => Task.CompletedTask;

        public Task<InngestEvent<TData>?> WaitForEventAsync<TData>(string id, WaitForEventOptions options)
        {
            Waits.Add((id, options));
            return Task.FromResult<InngestEvent<TData>?>(null);
        }

        public Task<TResult> InvokeAsync<TResult>(string id, InvokeOptions options) => throw new NotSupportedException();

        public Task<SendEventsResult> SendEventAsync(string id, IReadOnlyList<InngestEvent> events)
        {
            Sends.Add((id, events));
            return Task.FromResult(new SendEventsResult([.. events.Select((_, i) => $"{id}-{i}")]));
        }
    }
}
