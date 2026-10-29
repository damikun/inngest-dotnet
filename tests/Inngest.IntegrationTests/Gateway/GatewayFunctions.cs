using System.Net.Http.Json;
using System.Text;
using Inngest.IntegrationTests.Evaluation.Functions;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.Gateway;

internal sealed record SignalWait(string TestId, string Signal, int TimeoutSeconds, SignalConflict OnConflict = SignalConflict.Fail) : IScenarioEvent;

internal sealed record SignalSend(string TestId, string Signal, string Approver) : IScenarioEvent;

internal sealed record Approval(string Approver);

internal sealed record SignalOutcome(bool Received, string? Name, string? Approver);

internal sealed record FetchCall(string TestId, string ServerUrl, string LocalUrl) : IScenarioEvent;

internal sealed record FetchOutcome(int Status, string? EchoHeader, string? ContentType, ReceivedRequest Echo);

internal sealed record ClientOutcome(int FromFunction, int FromStep);

internal sealed record InferCall(string TestId, string Url) : IScenarioEvent;

internal sealed record ChatRequest(string Model, IReadOnlyList<ChatMessage> Messages);

internal sealed record ChatMessage(string Role, string Content);

internal sealed record ChatCompletion(string Id, string Model, IReadOnlyList<ChatChoice> Choices);

internal sealed record ChatChoice(int Index, ChatMessage Message);

internal static class GatewayFunctions
{
    public const string WaitForSignal = "gateway/signal.wait";
    public const string SendSignal = "gateway/signal.send";
    public const string Fetch = "gateway/fetch";
    public const string FetchThroughClient = "gateway/fetch.client";
    public const string Infer = "gateway/infer";

    // A named client whose requests become fetch steps inside functions.
    public const string DurableClient = "durable";

    // Never a real key: the fake endpoint only records it.
    public const string FakeKey = "fake-key-for-tests";

    public static void Add(IInngestBuilder inngest)
    {
        inngest.Services.AddHttpClient(DurableClient).AddInngestStepFetch();

        inngest.AddFunction<SignalWait>(
            new FunctionOptions { Id = "signal-wait", Triggers = [Trigger.Event(WaitForSignal)] },
            static async (context, _) =>
            {
                var data = context.Event.Data;
                var signal = await context.Step.WaitForSignalAsync<Approval>(
                    "wait-for-approval",
                    new WaitForSignalOptions(data.Signal, TimeSpan.FromSeconds(data.TimeoutSeconds)) { OnConflict = data.OnConflict });
                return signal is null ? new SignalOutcome(false, null, null) : new SignalOutcome(true, signal.Name, signal.Data.Approver);
            });

        inngest.AddFunction<SignalSend>(
            new FunctionOptions { Id = "signal-send", Triggers = [Trigger.Event(SendSignal)] },
            static async (context, _) =>
            {
                var data = context.Event.Data;
                return await context.Step.SendSignalAsync("send-approval", new InngestSignal(data.Signal, new Approval(data.Approver)));
            });

        // The step after the fetch makes the function replay past it twice more.
        inngest.AddFunction<FetchCall>(
            new FunctionOptions { Id = "fetch-step", Triggers = [Trigger.Event(Fetch)] },
            static async (context, _) =>
            {
                var data = context.Event.Data;
                using var request = new HttpRequestMessage(HttpMethod.Post, data.ServerUrl + "?source=step")
                {
                    Content = new StringContent("""{"hello":"world"}""", Encoding.UTF8, "application/json"),
                };
                request.Headers.Add(FakeEndpoints.TestIdHeader, data.TestId);

                using var response = await context.Step.FetchAsync("call-echo", request);
                var echo = await response.Content.ReadFromJsonAsync<ReceivedRequest>();
                await context.Step.RunAsync("after", () => context.Observed().Record("after"));
                return new FetchOutcome(
                    (int)response.StatusCode,
                    response.Headers.TryGetValues("X-Echo", out var values) ? string.Join(",", values) : null,
                    response.Content.Headers.ContentType?.MediaType,
                    echo!);
            });

        inngest.AddFunction<FetchCall>(
            new FunctionOptions { Id = "fetch-client", Triggers = [Trigger.Event(FetchThroughClient)] },
            static async (context, _) =>
            {
                var data = context.Event.Data;
                var http = context.Services.GetRequiredService<IHttpClientFactory>().CreateClient(DurableClient);

                using var durable = await http.SendAsync(Get(data.ServerUrl + "?source=function", data.TestId));
                var fromStep = await context.Step.RunAsync("direct", async () =>
                {
                    using var direct = await http.SendAsync(Get(data.LocalUrl + "?source=step-handler", data.TestId));
                    return (int)direct.StatusCode;
                });

                return new ClientOutcome((int)durable.StatusCode, fromStep);
            });

        inngest.AddFunction<InferCall>(
            new FunctionOptions { Id = "infer", Triggers = [Trigger.Event(Infer)] },
            static async (context, _) =>
            {
                var data = context.Event.Data;
                var completion = await context.Step.InferAsync<ChatCompletion>(
                    "say-hello",
                    new InferOptions(AIFormat.OpenAIChat, new Uri(data.Url), new ChatRequest("fake-model", [new ChatMessage("user", "Say hello")]))
                    {
                        AuthKey = FakeKey,
                        Headers = new Dictionary<string, string> { [FakeEndpoints.TestIdHeader] = data.TestId },
                    });
                return completion.Choices[0].Message.Content;
            });
    }

    private static HttpRequestMessage Get(string url, string testId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(FakeEndpoints.TestIdHeader, testId);
        return request;
    }
}
