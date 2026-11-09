using Inngest.IntegrationTests.Evaluation.Functions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.AI;

internal static class ModelFunctions
{
    public const string Usage = "ai/model.usage";
    public const string CheckpointedUsage = "ai/model.usage-checkpointed";
    public const string Infer = "ai/model.infer";

    public static void Add(IInngestBuilder inngest)
    {
        // A model call in a step of Microsoft.Extensions.AI, with OpenTelemetry inside the step, then a sleep, so that
        // the step's op reaches Inngest in the answer to a call and the run goes on.
        foreach (var (id, trigger, checkpointing) in new[] { ("model-usage", Usage, false), ("model-usage-checkpointed", CheckpointedUsage, true) })
        {
            inngest.AddFunction<RunEvent>(
                new FunctionOptions { Id = id, Triggers = [Trigger.Event(trigger)], Checkpointing = checkpointing ? new CheckpointingOptions() : null },
                static async (context, cancellationToken) =>
                {
                    var chat = new ChatClientBuilder(new UsageChatClient()).UseInngestSteps().UseOpenTelemetry().Build();
                    var reply = await chat.GetResponseAsync("Say hello", new ChatOptions { ModelId = "gpt-4o-mini", Temperature = 0.3f }, cancellationToken);
                    await context.Step.SleepAsync("pause", TimeSpan.FromSeconds(1));
                    return reply.Text;
                });
        }

        // An inference step for each format, sent by the dev server to the stand-ins of the providers.
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "model-infer", Triggers = [Trigger.Event(Infer)] },
            static async (context, _) =>
            {
                var providers = context.Services.GetRequiredService<FakeProviders>();
                var testId = context.Event.Data.TestId;
                var step = context.Step;

                var openAI = await step.InferAsync(
                    "openai",
                    AIModels.OpenAI(new OpenAIModelOptions("gpt-4o-mini") { ApiKey = "sk-openai", BaseUrl = providers.For(testId, "openai") }),
                    new OpenAIChatRequest { Messages = [OpenAIChatMessage.User("Hi")] });
                var responses = await step.InferAsync(
                    "responses",
                    AIModels.OpenAIResponses(new OpenAIResponsesModelOptions("gpt-5-mini") { ApiKey = "sk-openai", BaseUrl = providers.For(testId, "responses") }),
                    new OpenAIResponsesRequest { Input = [OpenAIResponsesInputItem.User("Hi")] });
                var anthropic = await step.InferAsync(
                    "anthropic",
                    AIModels.Anthropic(new AnthropicModelOptions("claude-sonnet-4-5", 256) { ApiKey = "sk-ant", BaseUrl = providers.For(testId, "anthropic") }),
                    new AnthropicRequest { Messages = [AnthropicMessage.User("Hi")] });
                var gemini = await step.InferAsync(
                    "gemini",
                    AIModels.Gemini(new GeminiModelOptions("gemini-2.5-flash") { ApiKey = "gemini-key", BaseUrl = providers.For(testId, "gemini") }),
                    new GeminiRequest { Contents = [GeminiContent.User("Hi")] });
                var grok = await step.InferAsync(
                    "grok",
                    AIModels.Grok(new OpenAIModelOptions("grok-3") { ApiKey = "xai-key", BaseUrl = providers.For(testId, "grok") }),
                    new OpenAIChatRequest { Messages = [OpenAIChatMessage.User("Hi")] });
                var azure = await step.InferAsync(
                    "azure",
                    AIModels.AzureOpenAI(new AzureOpenAIModelOptions(providers.For(testId, "azure"), "gpt-4o", "2024-10-21") { ApiKey = "azure-key" }),
                    new OpenAIChatRequest { Messages = [OpenAIChatMessage.User("Hi")] });

                return new[] { openAI.Text, responses.OutputText, anthropic.Text, gemini.Text, grok.Text, azure.Text };
            });
    }

    // A model that answers with usage, as a provider's client reports it.
    private sealed class UsageChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hello there"))
            {
                ResponseId = "chatcmpl-usage",
                ModelId = "gpt-4o-mini-2024-07-18",
                FinishReason = ChatFinishReason.Stop,
                Usage = new UsageDetails { InputTokenCount = 21, OutputTokenCount = 5, TotalTokenCount = 26, CachedInputTokenCount = 16 },
            });

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
