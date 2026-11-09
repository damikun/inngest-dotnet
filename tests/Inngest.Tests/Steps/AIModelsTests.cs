using System.Text.Json;
using System.Text.Json.Serialization;
using Inngest.Json;
using Inngest.Tests.Execution;

namespace Inngest.Tests.Steps;

// The models of AIModels against the models of @inngest/ai: the URL, headers, key and body each fills in.
public sealed class AIModelsTests : IDisposable
{
    private static readonly OpenAIChatRequest Hello = new() { Messages = [OpenAIChatMessage.User("Hello")] };

    private readonly EngineHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public void OpenAI_calls_chat_completions_with_the_model_in_the_body()
    {
        var model = AIModels.OpenAI(new OpenAIModelOptions("gpt-4o-mini") { ApiKey = "sk-test" });

        var options = model.CreateOptions(Hello);

        options.Format.ShouldBe(AIFormat.OpenAIChat);
        options.Url.AbsoluteUri.ShouldBe("https://api.openai.com/v1/chat/completions");
        options.AuthKey.ShouldBe("sk-test");
        options.Headers.ShouldBeNull();
        Json(options.Body).ShouldBe("""{"messages":[{"role":"user","content":"Hello"}],"model":"gpt-4o-mini"}""");
    }

    [Fact]
    public void A_request_wins_over_the_defaults_and_names_its_own_model()
    {
        var model = AIModels.OpenAI(new OpenAIModelOptions("gpt-4o-mini")
        {
            ApiKey = "sk-test",
            DefaultParameters = new OpenAIChatRequest { Temperature = 0.2, MaxCompletionTokens = 500 },
        });

        var options = model.CreateOptions(Hello with { Model = "gpt-4o", Temperature = 0.9 });

        Json(options.Body).ShouldBe("""{"model":"gpt-4o","messages":[{"role":"user","content":"Hello"}],"temperature":0.9,"max_completion_tokens":500}""");
    }

    [Fact]
    public void A_base_url_without_a_trailing_slash_keeps_its_path()
    {
        var model = AIModels.OpenAI(new OpenAIModelOptions("llama3") { ApiKey = "none", BaseUrl = new Uri("http://localhost:11434/v1") });

        model.Url.AbsoluteUri.ShouldBe("http://localhost:11434/v1/chat/completions");
    }

    [Fact]
    public void OpenAI_Responses_calls_responses()
    {
        var model = AIModels.OpenAIResponses(new OpenAIResponsesModelOptions("gpt-5-mini") { ApiKey = "sk-test" });

        var options = model.CreateOptions(new OpenAIResponsesRequest
        {
            Input = [OpenAIResponsesInputItem.User("Hello")],
            Reasoning = new OpenAIResponsesReasoning { Effort = "low" },
        });

        options.Format.ShouldBe(AIFormat.OpenAIResponses);
        options.Url.AbsoluteUri.ShouldBe("https://api.openai.com/v1/responses");
        Json(options.Body).ShouldBe("""{"input":[{"role":"user","content":"Hello"}],"reasoning":{"effort":"low"},"model":"gpt-5-mini"}""");
    }

    [Fact]
    public void Anthropic_sends_its_version_header_the_betas_and_a_max_tokens()
    {
        var model = AIModels.Anthropic(new AnthropicModelOptions("claude-sonnet-4-5", MaxTokens: 1024)
        {
            ApiKey = "sk-ant-test",
            BetaHeaders = ["prompt-caching-2024-07-31", "pdfs-2024-09-25"],
        });

        var options = model.CreateOptions(new AnthropicRequest { System = "Be brief.", Messages = [AnthropicMessage.User("Hello")] });

        options.Format.ShouldBe(AIFormat.Anthropic);
        options.Url.AbsoluteUri.ShouldBe("https://api.anthropic.com/v1/messages");
        options.AuthKey.ShouldBe("sk-ant-test");
        options.Headers.ShouldBe(new Dictionary<string, string>
        {
            ["anthropic-version"] = "2023-06-01",
            ["anthropic-beta"] = "prompt-caching-2024-07-31,pdfs-2024-09-25",
        });
        Json(options.Body).ShouldBe(
            """{"messages":[{"role":"user","content":[{"type":"text","text":"Hello"}]}],"system":"Be brief.","max_tokens":1024,"model":"claude-sonnet-4-5"}""");
    }

    [Fact]
    public void Anthropic_takes_the_max_tokens_of_the_request_then_of_the_defaults()
    {
        var model = AIModels.Anthropic(new AnthropicModelOptions("claude-sonnet-4-5", MaxTokens: 1024)
        {
            DefaultParameters = new AnthropicRequest { MaxTokens = 2048 },
        });

        Body(model.CreateOptions(new AnthropicRequest { Messages = [AnthropicMessage.User("Hi")] })).GetProperty("max_tokens").GetInt32().ShouldBe(2048);
        Body(model.CreateOptions(new AnthropicRequest { MaxTokens = 10, Messages = [AnthropicMessage.User("Hi")] })).GetProperty("max_tokens").GetInt32().ShouldBe(10);
    }

    [Fact]
    public void Gemini_names_the_model_and_the_key_in_the_url_and_merges_the_generation_config()
    {
        var model = AIModels.Gemini(new GeminiModelOptions("gemini-2.5-flash")
        {
            ApiKey = "AIza+test",
            DefaultParameters = new GeminiRequest
            {
                GenerationConfig = new GeminiGenerationConfig
                {
                    Temperature = 0.1,
                    MaxOutputTokens = 256,
                    ThinkingConfig = new GeminiThinkingConfig { ThinkingBudget = 0, IncludeThoughts = false },
                },
            },
        });

        var options = model.CreateOptions(new GeminiRequest
        {
            Contents = [GeminiContent.User("Hello")],
            GenerationConfig = new GeminiGenerationConfig { Temperature = 0.7, ThinkingConfig = new GeminiThinkingConfig { ThinkingBudget = 512 } },
        });

        options.Format.ShouldBe(AIFormat.Gemini);
        options.Url.AbsoluteUri.ShouldBe("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key=AIza%2Btest");
        options.AuthKey.ShouldBe("AIza+test");
        Json(options.Body).ShouldBe(
            """{"contents":[{"parts":[{"text":"Hello"}],"role":"user"}],"generationConfig":{"temperature":0.7,"thinkingConfig":{"thinkingBudget":512,"includeThoughts":false},"maxOutputTokens":256}}""");
    }

    [Fact]
    public void Grok_and_DeepSeek_speak_the_OpenAI_format_at_their_own_addresses()
    {
        var grok = AIModels.Grok(new OpenAIModelOptions("grok-3") { ApiKey = "xai-test" });
        var deepSeek = AIModels.DeepSeek(new OpenAIModelOptions("deepseek-chat") { ApiKey = "ds-test" });

        grok.Format.ShouldBe(AIFormat.Grok);
        grok.Url.AbsoluteUri.ShouldBe("https://api.x.ai/v1/chat/completions");
        deepSeek.Format.ShouldBe(AIFormat.OpenAIChat);
        deepSeek.Url.AbsoluteUri.ShouldBe("https://api.deepseek.com/v1/chat/completions");
        Body(deepSeek.CreateOptions(Hello)).GetProperty("model").GetString().ShouldBe("deepseek-chat");
    }

    [Fact]
    public void Azure_OpenAI_calls_the_deployment_with_the_api_version_and_the_api_key_header()
    {
        var model = AIModels.AzureOpenAI(new AzureOpenAIModelOptions(new Uri("https://shop.openai.azure.com"), "gpt-4o", "2024-10-21") { ApiKey = "azure-test" });

        var options = model.CreateOptions(Hello);

        options.Format.ShouldBe(AIFormat.AzureOpenAI);
        options.Url.AbsoluteUri.ShouldBe("https://shop.openai.azure.com/openai/deployments/gpt-4o/chat/completions?api-version=2024-10-21");
        options.AuthKey.ShouldBe("azure-test");
        options.Headers.ShouldBe(new Dictionary<string, string> { ["api-key"] = "azure-test", ["Content-Type"] = "application/json" });
        Json(options.Body).ShouldBe("""{"messages":[{"role":"user","content":"Hello"}]}""");
    }

    // The variables are the ones @inngest/ai reads; nothing else in the tests sets them.
    [Theory]
    [InlineData("OPENAI_API_KEY")]
    [InlineData("ANTHROPIC_API_KEY")]
    [InlineData("GEMINI_API_KEY")]
    [InlineData("XAI_API_KEY")]
    [InlineData("DEEPSEEK_API_KEY")]
    [InlineData("AZURE_OPENAI_API_KEY")]
    public void A_key_left_out_is_read_from_the_providers_variable(string variable)
    {
        var saved = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "from-" + variable);
            KeyOf(variable).ShouldBe("from-" + variable);

            Environment.SetEnvironmentVariable(variable, null);
            KeyOf(variable).ShouldBe(string.Empty);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, saved);
        }
    }

    [Fact]
    public void Printing_the_options_does_not_reveal_the_key()
    {
        object[] options =
        [
            new OpenAIModelOptions("gpt-4o") { ApiKey = "secret-key" },
            new OpenAIResponsesModelOptions("gpt-4o") { ApiKey = "secret-key" },
            new AnthropicModelOptions("claude", 100) { ApiKey = "secret-key" },
            new GeminiModelOptions("gemini") { ApiKey = "secret-key" },
            new AzureOpenAIModelOptions(new Uri("https://shop.openai.azure.com"), "gpt-4o", "2024-10-21") { ApiKey = "secret-key" },
        ];

        options.ShouldAllBe(option => !option.ToString()!.Contains("secret-key"));
    }

    [Fact]
    public async Task The_step_sends_what_the_model_made_of_the_request()
    {
        var model = AIModels.Anthropic(new AnthropicModelOptions("claude-sonnet-4-5", 512) { ApiKey = "sk-ant-test" });
        var function = TestFunction.Define(async context =>
        {
            await context.Step.InferAsync("summarize", model, new AnthropicRequest { Messages = [AnthropicMessage.User("Summarize this")] });
            return null;
        });

        var result = await _host.CallAsync(function, Call.New());

        result.Body.ShouldBe(
            $$$"""[{"id":"{{{Call.Hash("summarize")}}}","op":"AIGateway","displayName":"summarize","opts":{"type":"step.ai.infer","url":"https://api.anthropic.com/v1/messages","headers":{"anthropic-version":"2023-06-01"},"auth_key":"sk-ant-test","format":"anthropic","body":{"messages":[{"role":"user","content":[{"type":"text","text":"Summarize this"}]}],"max_tokens":512,"model":"claude-sonnet-4-5"}},"userland":{"id":"summarize"}}]""");
    }

    [Fact]
    public async Task The_response_comes_back_in_the_providers_format()
    {
        var openAI = AIModels.OpenAI(new OpenAIModelOptions("gpt-4o-mini"));
        var anthropic = AIModels.Anthropic(new AnthropicModelOptions("claude-sonnet-4-5", 512));
        var gemini = AIModels.Gemini(new GeminiModelOptions("gemini-2.5-flash"));
        var responses = AIModels.OpenAIResponses(new OpenAIResponsesModelOptions("gpt-5-mini"));
        var function = TestFunction.Define(async context =>
        {
            var chat = await context.Step.InferAsync("openai", openAI, Hello);
            var message = await context.Step.InferAsync(new StepOptions("anthropic"), anthropic, new AnthropicRequest { Messages = [AnthropicMessage.User("Hi")] });
            var content = await context.Step.InferAsync("gemini", gemini, new GeminiRequest { Contents = [GeminiContent.User("Hi")] });
            var response = await context.Step.InferAsync("responses", responses, new OpenAIResponsesRequest { Input = [OpenAIResponsesInputItem.User("Hi")] });
            return string.Join(
                " | ",
                chat.Text,
                chat.Usage!.TotalTokens,
                chat.Choices[0].FinishReason,
                message.Text,
                message.Usage!.CacheReadInputTokens,
                content.Text,
                content.UsageMetadata!.ThoughtsTokenCount,
                response.OutputText,
                response.Usage!.OutputTokensDetails!.ReasoningTokens,
                chat.AdditionalProperties!["provider_extra"].GetString());
        });

        var call = Call.New()
            .Data("openai", """{"id":"chatcmpl-1","object":"chat.completion","created":1,"model":"gpt-4o-mini","choices":[{"index":0,"message":{"role":"assistant","content":"Hello!","refusal":null},"finish_reason":"stop"}],"usage":{"prompt_tokens":8,"completion_tokens":2,"total_tokens":10,"prompt_tokens_details":{"cached_tokens":0}},"provider_extra":"kept"}""")
            .Data("anthropic", """{"id":"msg_1","type":"message","role":"assistant","model":"claude-sonnet-4-5","content":[{"type":"thinking","thinking":"...","signature":"x"},{"type":"text","text":"Hi there"}],"stop_reason":"end_turn","usage":{"input_tokens":5,"output_tokens":3,"cache_read_input_tokens":4}}""")
            .Data("gemini", """{"candidates":[{"content":{"role":"model","parts":[{"text":"thinking","thought":true},{"text":"Hey"}]},"finishReason":"STOP","index":0}],"usageMetadata":{"promptTokenCount":3,"candidatesTokenCount":1,"totalTokenCount":9,"thoughtsTokenCount":5},"modelVersion":"gemini-2.5-flash"}""")
            .Data("responses", """{"id":"resp_1","object":"response","status":"completed","model":"gpt-5-mini","output":[{"type":"reasoning","id":"rs_1","summary":[]},{"type":"message","id":"msg_1","role":"assistant","content":[{"type":"output_text","text":"Yo","annotations":[]}]}],"usage":{"input_tokens":4,"output_tokens":20,"total_tokens":24,"output_tokens_details":{"reasoning_tokens":18}}}""");

        var result = await _host.CallAsync(function, call);

        result.Body.ShouldBe("\"Hello! | 10 | stop | Hi there | 4 | Hey | 5 | Yo | 18 | kept\"");
    }

    // An app under Native AOT has only its own source-generated contracts, which do not know the responses.
    [Fact]
    public async Task The_response_is_read_without_contracts_of_the_app()
    {
        using var host = new EngineHost(settings: EngineHost.Settings with
        {
            SerializerOptions = SerializerOptionsSetup.Prepare(new JsonSerializerOptions { TypeInfoResolver = ResponselessJsonContext.Default }),
        });
        var model = AIModels.Anthropic(new AnthropicModelOptions("claude-sonnet-4-5", 512));
        var function = TestFunction.Define(async context =>
        {
            var response = await context.Step.InferAsync("anthropic", model, new AnthropicRequest { Messages = [AnthropicMessage.User("Hi")] });
            return $"{response.Text} {string.Join(",", response.Content[0].AdditionalProperties!.Keys)} {string.Join(",", response.AdditionalProperties!.Keys)}";
        });

        var result = await host.CallAsync(
            function,
            Call.New().Data("anthropic", """{"content":[{"type":"text","text":"Hi","citations":[]}],"container":null}"""));

        result.Body.ShouldBe("\"Hi citations container\"");
    }

    private static string KeyOf(string variable) => variable switch
    {
        "OPENAI_API_KEY" => AIModels.OpenAI(new OpenAIModelOptions("m")).AuthKey,
        "ANTHROPIC_API_KEY" => AIModels.Anthropic(new AnthropicModelOptions("m", 1)).AuthKey,
        "GEMINI_API_KEY" => AIModels.Gemini(new GeminiModelOptions("m")).AuthKey,
        "XAI_API_KEY" => AIModels.Grok(new OpenAIModelOptions("m")).AuthKey,
        "DEEPSEEK_API_KEY" => AIModels.DeepSeek(new OpenAIModelOptions("m")).AuthKey,
        _ => AIModels.AzureOpenAI(new AzureOpenAIModelOptions(new Uri("https://a.openai.azure.com"), "d", "v")).AuthKey,
    };

    private static JsonElement Body(InferOptions options) => (JsonElement)options.Body;

    private static string Json(object body) => ((JsonElement)body).GetRawText();
}

[JsonSerializable(typeof(string))]
internal sealed partial class ResponselessJsonContext : JsonSerializerContext
{
}
