using Inngest.Tests.Execution;

namespace Inngest.Tests.Gateway;

public sealed class InferStepTests : IDisposable
{
    private static readonly Uri ChatUrl = new("https://api.openai.com/v1/chat/completions");

    private readonly EngineHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Infer_sends_the_request_in_the_shape_of_inngest_js()
    {
        var result = await Report(step => step.InferAsync<Completion>("summarize", new InferOptions(AIFormat.OpenAIChat, ChatUrl, Chat("Summarize this"))
        {
            AuthKey = "fake-key",
            Headers = new Dictionary<string, string> { ["OpenAI-Organization"] = "org-1" },
        }));

        result.Body.ShouldBe(
            $$$"""[{"id":"{{{Call.Hash("summarize")}}}","op":"AIGateway","displayName":"summarize","opts":{"type":"step.ai.infer","url":"https://api.openai.com/v1/chat/completions","headers":{"OpenAI-Organization":"org-1"},"auth_key":"fake-key","format":"openai-chat","body":{"model":"gpt-4o","messages":[{"role":"user","content":"Summarize this"}]}},"userland":{"id":"summarize"}}]""");
    }

    [Fact]
    public async Task Without_headers_or_key_it_sends_an_empty_key_like_inngest_js()
    {
        var result = await Report(step => step.InferAsync<Completion>("summarize", new InferOptions(AIFormat.OpenAIChat, ChatUrl, Chat("Hi"))));

        var opts = result.Op().GetProperty("opts");
        opts.TryGetProperty("headers", out _).ShouldBeFalse();
        opts.GetProperty("auth_key").GetString().ShouldBe(string.Empty);
    }

    [Theory]
    [InlineData(AIFormat.OpenAIChat, "openai-chat")]
    [InlineData(AIFormat.OpenAIResponses, "openai-responses")]
    [InlineData(AIFormat.Anthropic, "anthropic")]
    [InlineData(AIFormat.Gemini, "gemini")]
    [InlineData(AIFormat.Grok, "grok")]
    [InlineData(AIFormat.AzureOpenAI, "azure-openai")]
    public async Task Each_format_has_the_name_inngest_knows(AIFormat format, string name)
    {
        var result = await Report(step => step.InferAsync<Completion>("summarize", new InferOptions(format, ChatUrl, Chat("Hi"))));

        result.Op().GetProperty("opts").GetProperty("format").GetString().ShouldBe(name);
    }

    [Fact]
    public async Task The_memoized_provider_response_is_deserialized()
    {
        var function = TestFunction.Define(async context =>
        {
            var completion = await context.Step.InferAsync<Completion>("summarize", new InferOptions(AIFormat.OpenAIChat, ChatUrl, Chat("Hi")));
            var raw = await context.Step.InferAsync("raw", new InferOptions(AIFormat.OpenAIChat, ChatUrl, Chat("Hi")));
            return $"{completion.Choices[0].Message.Content} {raw.GetProperty("id").GetString()}";
        });

        var response = """{"id":"chatcmpl-1","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"Hello"}}]}""";
        var result = await _host.CallAsync(function, Call.New().Data("summarize", response).Data("raw", response));

        result.Body.ShouldBe("\"Hello chatcmpl-1\"");
    }

    [Fact]
    public async Task A_provider_that_keeps_failing_fails_the_step()
    {
        var function = TestFunction.Define(async context =>
        {
            try
            {
                return await context.Step.InferAsync<Completion>("summarize", new InferOptions(AIFormat.OpenAIChat, ChatUrl, Chat("Hi")));
            }
            catch (StepFailedException exception)
            {
                return exception.Error.Name;
            }
        });

        var result = await _host.CallAsync(function, Call.New().Failed("summarize", "AIGatewayError", "Error making AI request: unsuccessful status code: 429"));

        result.Body.ShouldBe("\"AIGatewayError\"");
    }

    [Fact]
    public async Task Infer_checks_its_options()
    {
        var errors = new List<Exception>();
        var function = TestFunction.Define(context =>
        {
            errors.Add(Should.Throw<ArgumentException>(() => context.Step.InferAsync<Completion>("a", new InferOptions(AIFormat.OpenAIChat, new Uri("/v1/chat", UriKind.Relative), Chat("Hi")))));
            errors.Add(Should.Throw<ArgumentOutOfRangeException>(() => context.Step.InferAsync<Completion>("b", new InferOptions((AIFormat)42, ChatUrl, Chat("Hi")))));
            errors.Add(Should.Throw<ArgumentNullException>(() => context.Step.InferAsync<Completion>("c", new InferOptions(AIFormat.OpenAIChat, ChatUrl, null!))));
            errors.Add(Should.Throw<ArgumentException>(() => context.Step.InferAsync<Completion>("d", new InferOptions(AIFormat.OpenAIChat, ChatUrl, "{\"model\":\"gpt-4o\"}"))));
            return Task.FromResult<object?>(null);
        });

        await _host.CallAsync(function, Call.New());

        errors.Select(error => error.GetType()).ShouldBe(
            [typeof(ArgumentException), typeof(ArgumentOutOfRangeException), typeof(ArgumentNullException), typeof(ArgumentException)]);
        errors[3].Message.ShouldBe("The body of infer step \"d\" must serialize to a JSON object, but String does not.");
    }

    private static ChatRequest Chat(string text) => new("gpt-4o", [new ChatMessage("user", text)]);

    private Task<CallResult> Report(Func<IStepTools, Task> step) =>
        _host.CallAsync(
            TestFunction.Define(async context =>
            {
                await step(context.Step);
                return null;
            }),
            Call.New());

    internal sealed record ChatRequest(string Model, IReadOnlyList<ChatMessage> Messages);

    internal sealed record ChatMessage(string Role, string Content);

    internal sealed record Completion(string Id, IReadOnlyList<Choice> Choices);

    internal sealed record Choice(int Index, ChatMessage Message);
}
