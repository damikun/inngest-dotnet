using System.Runtime.CompilerServices;
using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Harness;
using Microsoft.Extensions.AI;

namespace Inngest.IntegrationTests.AI;

internal static class ChatFunctions
{
    public const string Agent = "ai/chat.agent";
    public const string Streaming = "ai/chat.stream";
    public const string StreamingFailure = "ai/chat.stream-failure";

    public static RealtimeTopic Tokens(string testId) => new($"run:{testId}", "tokens");

    public static void Add(IInngestBuilder inngest)
    {
        // A model that asks for a tool, a tool, the model again, and a sleep after them. Every call of the function replays all
        // of it, and what the model and the tool record is how often they really ran.
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "chat-agent", Triggers = [Trigger.Event(Agent)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                observed.Record("body");
                var weather = AIFunctionFactory.Create(
                    (string city) =>
                    {
                        observed.Record("tool", city);
                        return $"Sunny in {city}";
                    },
                    "get_weather",
                    "Gets the weather of a city");
                var chat = new ChatClientBuilder(new ScriptedChatClient(observed)).UseInngestFunctionInvocation().UseInngestSteps().Build();

                var reply = await chat.GetResponseAsync("What is the weather in Prague?", new ChatOptions { Tools = [weather] });
                await context.Step.SleepAsync("pause", TimeSpan.FromSeconds(1));
                return reply.Text;
            });

        // The model streams its answer word by word, and the step publishes the words to a topic while it reads them.
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "chat-stream", Triggers = [Trigger.Event(Streaming)] },
            static async (context, _) =>
            {
                var chat = new ChatClientBuilder(new ScriptedChatClient(context.Observed())).UseInngestSteps().Build();
                var options = new ChatOptions().WithInngestRealtimeStream(Tokens(context.Event.Data.TestId));

                var reply = await chat.GetStreamingResponseAsync("Where is my order?", options).ToChatResponseAsync();
                return reply.Text;
            });

        // The model fails in the middle of its first stream, after two words, and the retry of the step streams it whole.
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "chat-stream-failure", Triggers = [Trigger.Event(StreamingFailure)] },
            static async (context, _) =>
            {
                var chat = new ChatClientBuilder(new ScriptedChatClient(context.Observed(), failFirstStream: true)).UseInngestSteps().Build();
                var options = new ChatOptions().WithInngestRealtimeStream(Tokens(context.Event.Data.TestId));

                var reply = await chat.GetStreamingResponseAsync("Where is my order?", options).ToChatResponseAsync();
                return reply.Text;
            });
    }

    private sealed class ScriptedChatClient(TestRecording observed, bool failFirstStream = false) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            observed.Record("model");
            var answered = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Any();
            return Task.FromResult(answered
                ? new ChatResponse(new ChatMessage(ChatRole.Assistant, "It is sunny in Prague."))
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call_1", "get_weather", new Dictionary<string, object?> { ["city"] = "Prague" })])));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var attempt = observed.Record("model");
            var words = new[] { "Your ", "order ", "is ", "on ", "its ", "way" };
            for (var i = 0; i < words.Length; i++)
            {
                await Task.Delay(150, cancellationToken);
                if (failFirstStream && attempt == 1 && i == 2)
                {
                    throw new HttpRequestException("The connection to the model was reset.");
                }

                yield return new ChatResponseUpdate(ChatRole.Assistant, words[i]);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
