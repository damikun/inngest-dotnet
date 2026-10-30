using System.Net.Http.Json;
using System.Text.Json;
using Inngest.Tests.Execution;

namespace Inngest.Tests.Steps;

// The StepOptions overloads of the signal, fetch and infer tools: display names, race mode and fakes.
public sealed class GatewayStepOptionsTests : IDisposable
{
    private static readonly WaitForSignalOptions Approval = new("approval/order-1", TimeSpan.FromHours(1));
    private static readonly InferOptions Chat = new(AIFormat.OpenAIChat, new Uri("https://api.openai.com/v1/chat/completions"), new { model = "gpt-4o" });

    private readonly EngineHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Each_tool_shows_its_name_and_keeps_its_id()
    {
        var function = TestFunction.Define(async context =>
        {
            await Task.WhenAll(
                context.Step.WaitForSignalAsync(new StepOptions("wait") { Name = "Wait for approval" }, Approval),
                context.Step.SendSignalAsync(new StepOptions("send") { Name = "Approve" }, new InngestSignal("approval/order-2")),
                context.Step.FetchAsync(new StepOptions("fetch") { Name = "Load the order" }, new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/orders/1")),
                context.Step.InferAsync(new StepOptions("infer") { Name = "Summarize" }, Chat));
            return null;
        });

        var result = await _host.CallAsync(function, Call.New());

        result.OpCodes.ShouldBe(["WaitForSignal", "StepPlanned", "Gateway", "AIGateway"]);
        result.DisplayNames.ShouldBe(["Wait for approval", "Approve", "Load the order", "Summarize"]);
        result.Ops.Select(op => op.GetProperty("id").GetString()).ShouldBe([Call.Hash("wait"), Call.Hash("send"), Call.Hash("fetch"), Call.Hash("infer")]);
        result.Ops.Select(op => op.GetProperty("userland").GetProperty("id").GetString()).ShouldBe(["wait", "send", "fetch", "infer"]);
    }

    [Fact]
    public async Task Each_tool_takes_part_in_a_race()
    {
        var function = TestFunction.Define(async context =>
        {
            await context.Step.ParallelAsync(ParallelMode.Race, () => Task.WhenAny(
                context.Step.WaitForSignalAsync("wait", Approval),
                context.Step.SendSignalAsync("send", new InngestSignal("approval/order-2")),
                context.Step.FetchAsync("fetch", new HttpRequestMessage(HttpMethod.Post, "https://api.example.com/orders") { Content = JsonContent.Create(new { id = 1 }) }),
                context.Step.InferAsync("infer", Chat)));
            return null;
        });

        var result = await _host.CallAsync(function, Call.New());

        result.Ops.Length.ShouldBe(4);
        result.Ops.ShouldAllBe(op => op.GetProperty("opts").GetProperty("parallelMode").GetString() == "race");
    }

    [Fact]
    public async Task A_fake_gets_the_id_from_the_step_options()
    {
        var fake = new RecordingTools();
        IStepTools step = fake;

        await step.WaitForSignalAsync<JsonElement>(new StepOptions("wait") { Name = "Wait" }, Approval);
        await step.WaitForSignalAsync(new StepOptions("wait-json"), Approval);
        await step.SendSignalAsync(new StepOptions("send"), new InngestSignal("approval/order-2"));
        await step.FetchAsync(new StepOptions("fetch"), new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/"));
        await step.InferAsync<JsonElement>(new StepOptions("infer"), Chat);
        await step.InferAsync(new StepOptions("infer-json"), Chat);

        fake.Calls.ShouldBe(["WaitForSignal wait", "WaitForSignal wait-json", "SendSignal send", "Fetch fetch", "Infer infer", "Infer infer-json"]);
    }

    private sealed class RecordingTools : IStepTools
    {
        public List<string> Calls { get; } = [];

        public Task<T> RunAsync<T>(string id, Func<CancellationToken, Task<T>> handler) => throw new NotSupportedException();

        public Task SleepAsync(string id, TimeSpan duration) => throw new NotSupportedException();

        public Task SleepUntilAsync(string id, DateTimeOffset until) => throw new NotSupportedException();

        public Task<InngestEvent<TData>?> WaitForEventAsync<TData>(string id, WaitForEventOptions options) => throw new NotSupportedException();

        public Task<TResult> InvokeAsync<TResult>(string id, InvokeOptions options) => throw new NotSupportedException();

        public Task<SendEventsResult> SendEventAsync(string id, IReadOnlyList<InngestEvent> events) => throw new NotSupportedException();

        public Task<InngestSignal<TData>?> WaitForSignalAsync<TData>(string id, WaitForSignalOptions options)
        {
            Calls.Add($"WaitForSignal {id}");
            return Task.FromResult<InngestSignal<TData>?>(null);
        }

        public Task<string?> SendSignalAsync(string id, InngestSignal signal)
        {
            Calls.Add($"SendSignal {id}");
            return Task.FromResult<string?>(null);
        }

        public Task<HttpResponseMessage> FetchAsync(string id, HttpRequestMessage request)
        {
            Calls.Add($"Fetch {id}");
            return Task.FromResult(new HttpResponseMessage());
        }

        public Task<TResponse> InferAsync<TResponse>(string id, InferOptions options)
        {
            Calls.Add($"Infer {id}");
            return Task.FromResult(default(TResponse)!);
        }
    }
}
