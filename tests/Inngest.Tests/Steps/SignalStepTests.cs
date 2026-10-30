using System.Net;
using System.Text.Json;
using Inngest.Client;
using Inngest.Tests.Client;
using Inngest.Tests.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Inngest.Tests.Steps;

public sealed class SignalStepTests : IDisposable
{
    private readonly FakeHttpHandler _handler = new();
    private readonly EngineHost _host;

    public SignalStepTests()
    {
        _host = new EngineHost(services => services.AddSingleton(provider => new InngestClient(
            new FakeHttpClientFactory(_handler),
            EngineHost.Settings,
            TimeProvider.System,
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<InngestClient>.Instance,
            provider.GetRequiredService<IServiceProviderIsService>())));
    }

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Wait_for_signal_sends_the_signal_timeout_and_conflict_mode()
    {
        var result = await Report(step => step.WaitForSignalAsync<Approval>("wait-for-approval", new WaitForSignalOptions("approval/order-1", TimeSpan.FromHours(1))));

        result.Body.ShouldBe(
            $$$"""[{"id":"{{{Call.Hash("wait-for-approval")}}}","op":"WaitForSignal","name":"approval/order-1","displayName":"wait-for-approval","opts":{"signal":"approval/order-1","timeout":"1h","conflict":"fail"},"userland":{"id":"wait-for-approval"}}]""");
    }

    [Fact]
    public async Task Wait_for_signal_can_replace_another_runs_wait()
    {
        var result = await Report(step => step.WaitForSignalAsync<Approval>(
            "wait-for-approval",
            new WaitForSignalOptions("approval/order-1", TimeSpan.FromMilliseconds(300)) { OnConflict = SignalConflict.Replace }));

        result.Op().GetProperty("opts").GetRawText().ShouldBe("""{"signal":"approval/order-1","timeout":"1s","conflict":"replace"}""");
    }

    [Fact]
    public async Task Wait_for_signal_returns_the_signal_inngest_memoized()
    {
        var result = await _host.CallAsync(
            WaitFunction(),
            Call.New().Data("wait-for-approval", """{"data":{"approver":"alice"},"signal":"approval/order-1"}"""));

        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe("\"approval/order-1 approved by alice\"");
    }

    [Fact]
    public async Task Wait_for_signal_returns_null_when_the_wait_timed_out()
    {
        var result = await _host.CallAsync(WaitFunction(), Call.New().Memo("wait-for-approval", "null"));

        result.Body.ShouldBe("\"timed out\"");
    }

    [Fact]
    public async Task A_signal_sent_without_data_has_null_data()
    {
        JsonElement? data = null;
        var function = TestFunction.Define(async context =>
        {
            var signal = await context.Step.WaitForSignalAsync("wait-for-approval", new WaitForSignalOptions("approval/order-1", TimeSpan.FromHours(1)));
            data = signal!.Data;
            return signal.Name;
        });

        var result = await _host.CallAsync(function, Call.New().Data("wait-for-approval", """{"data":null,"signal":"approval/order-1"}"""));

        result.Body.ShouldBe("\"approval/order-1\"");
        data!.Value.ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Wait_for_signal_checks_its_options()
    {
        var errors = new List<Exception>();
        var function = TestFunction.Define(context =>
        {
            errors.Add(Should.Throw<ArgumentException>(() => context.Step.WaitForSignalAsync<Approval>("w", new WaitForSignalOptions("", TimeSpan.FromHours(1)))));
            errors.Add(Should.Throw<ArgumentOutOfRangeException>(() => context.Step.WaitForSignalAsync<Approval>("w", new WaitForSignalOptions("s", TimeSpan.FromSeconds(-1)))));
            errors.Add(Should.Throw<ArgumentNullException>(() => context.Step.SendSignalAsync("s", null!)));
            errors.Add(Should.Throw<ArgumentException>(() => context.Step.SendSignalAsync("s", new InngestSignal(""))));
            return Task.FromResult<object?>(null);
        });

        await _host.CallAsync(function, Call.New());

        errors.Count.ShouldBe(4);
    }

    [Fact]
    public async Task Send_signal_is_planned_as_a_run_step()
    {
        var function = TestFunction.Define(async context =>
        {
            await Task.WhenAll(
                context.Step.SendSignalAsync("notify", new InngestSignal("approval/order-1", new Approval("alice"))),
                context.Step.SleepAsync("sleep-a", TimeSpan.FromMinutes(1)));
            return null;
        });

        var result = await _host.CallAsync(function, Call.New());

        result.Op().GetRawText().ShouldBe(
            $$$"""{"id":"{{{Call.Hash("notify")}}}","op":"StepPlanned","name":"sendSignal","displayName":"notify","opts":{"type":"step.sendSignal","signal":"approval/order-1"},"userland":{"id":"notify"}}""");
    }

    [Fact]
    public async Task Send_signal_posts_the_signal_and_memoizes_the_resumed_run()
    {
        _handler.Respond(HttpStatusCode.OK, """{"data":{"run_id":"01JRESUMED"},"metadata":{"fetched_at":"2026-10-29T10:00:00Z"}}""");

        var result = await _host.CallAsync(SendFunction(), Call.New());

        var request = _handler.Single;
        request.Method.ShouldBe(HttpMethod.Post);
        request.Uri.ShouldBe(new Uri("http://localhost:8288/v1/signals"));
        request.BodyText.ShouldBe("""{"signal":"approval/order-1","data":{"approver":"alice"}}""");
        result.OpCodes.ShouldBe(["StepRun"]);
        result.Op().GetProperty("name").GetString().ShouldBe("sendSignal");
        result.Op().GetProperty("opts").GetRawText().ShouldBe("""{"type":"step.sendSignal","signal":"approval/order-1"}""");
        result.Op().GetProperty("data").GetRawText().ShouldBe("""{"runId":"01JRESUMED"}""");
    }

    [Fact]
    public async Task Send_signal_memoizes_null_when_no_run_waits_for_the_signal()
    {
        _handler.Respond(HttpStatusCode.NotFound, """{"error":"No signal found","data":null,"status":404}""");

        var result = await _host.CallAsync(SendFunction(), Call.New());

        result.OpCodes.ShouldBe(["StepRun"]);
        result.Op().GetProperty("data").GetRawText().ShouldBe("""{"runId":null}""");
    }

    [Fact]
    public async Task Send_signal_is_retried_when_inngest_fails()
    {
        _handler.Respond(HttpStatusCode.InternalServerError, """{"error":"Failed to receive signal"}""");

        var result = await _host.CallAsync(SendFunction(), Call.New());

        result.OpCodes.ShouldBe(["StepError"]);
        var error = result.Op().GetProperty("error");
        error.GetProperty("name").GetString().ShouldBe("InngestException");
        error.GetProperty("message").GetString().ShouldBe("Failed to send signal \"approval/order-1\": Failed to receive signal");
    }

    [Fact]
    public async Task A_memoized_send_returns_the_run_id_without_sending_again()
    {
        var resumed = await _host.CallAsync(SendFunction(), Call.New().Data("notify", """{"runId":"01JRESUMED"}"""));
        var missed = await _host.CallAsync(SendFunction(), Call.New().Data("notify", """{"runId":null}"""));

        resumed.Body.ShouldBe("\"01JRESUMED\"");
        missed.Body.ShouldBe("null");
        _handler.Requests.ShouldBeEmpty();
    }

    private static Inngest.Functions.FunctionDefinition WaitFunction() =>
        TestFunction.Define(async context =>
        {
            var signal = await context.Step.WaitForSignalAsync<Approval>("wait-for-approval", new WaitForSignalOptions("approval/order-1", TimeSpan.FromHours(1)));
            return signal is null ? "timed out" : $"{signal.Name} approved by {signal.Data.Approver}";
        });

    private static Inngest.Functions.FunctionDefinition SendFunction() =>
        TestFunction.Define(async context =>
            await context.Step.SendSignalAsync("notify", new InngestSignal("approval/order-1", new Approval("alice"))));

    private Task<CallResult> Report(Func<IStepTools, Task> step) =>
        _host.CallAsync(
            TestFunction.Define(async context =>
            {
                await step(context.Step);
                return null;
            }),
            Call.New());

    internal sealed record Approval(string Approver);
}
