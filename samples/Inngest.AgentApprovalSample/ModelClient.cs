using System.ClientModel;
using Anthropic;
using Anthropic.Exceptions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;

namespace Inngest.AgentApprovalSample;

public sealed class AgentSettings
{
    public string? Provider { get; set; }

    public string OpenAIModel { get; set; } = "gpt-4o-mini";

    public string AnthropicModel { get; set; } = "claude-opus-5-5";

    public Uri? OpenAIEndpoint { get; set; }

    public TimeSpan ApprovalTimeout { get; set; } = TimeSpan.FromDays(1);
}

public sealed class ModelClient(IConfiguration configuration, IOptions<AgentSettings> settings, IServiceProvider services)
{
    public const string OpenAI = "openai";
    public const string Anthropic = "anthropic";

    public string? Provider => settings.Value.Provider switch
    {
        { Length: > 0 } provider => provider,
        _ when HasKey(Anthropic) => Anthropic,
        _ when HasKey(OpenAI) || settings.Value.OpenAIEndpoint is not null => OpenAI,
        _ => null,
    };

    public string? Model => Provider switch
    {
        Anthropic => settings.Value.AnthropicModel,
        OpenAI => settings.Value.OpenAIModel,
        _ => null,
    };

    public IChatClient Create()
    {
        var model = Provider switch
        {
            Anthropic => new AnthropicClient { ApiKey = KeyOf(Anthropic) }.AsIChatClient(settings.Value.AnthropicModel, defaultMaxOutputTokens: 16000),
            OpenAI => new OpenAIClient(new ApiKeyCredential(KeyOf(OpenAI, optional: settings.Value.OpenAIEndpoint is not null)), new OpenAIClientOptions { Endpoint = settings.Value.OpenAIEndpoint })
                .GetChatClient(settings.Value.OpenAIModel)
                .AsIChatClient(),
            var provider => throw new NonRetriableException($"No model is set up (provider '{provider}'). See the sample's README."),
        };

        return model
            .AsBuilder()
            .UseInngestFunctionInvocation()
            .UseInngestSteps()
            .Use(FailOnRefusalAsync)
            .Build(services);
    }

    private bool HasKey(string provider) => !string.IsNullOrWhiteSpace(configuration[SectionOf(provider) + ":ApiKey"]);

    private string KeyOf(string provider, bool optional = false) =>
        configuration[SectionOf(provider) + ":ApiKey"] is { Length: > 0 } key ? key
        : optional ? "unused"
        : throw new NonRetriableException($"There is no {SectionOf(provider)}:ApiKey in the configuration.");

    private static string SectionOf(string provider) => provider == Anthropic ? "Anthropic" : "OpenAI";

    private static async Task FailOnRefusalAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task> next,
        CancellationToken cancellationToken)
    {
        try
        {
            await next(messages, options, cancellationToken);
        }
        catch (ClientResultException exception) when (exception.Status is 401 or 403)
        {
            throw new NonRetriableException($"The model provider refused the API key (HTTP {exception.Status}).");
        }
        catch (ClientResultException exception) when (exception.Status is 400 or 404)
        {
            throw new NonRetriableException($"The model provider refused the request (HTTP {exception.Status}): {exception.Message}");
        }
        catch (Exception exception) when (exception is AnthropicUnauthorizedException or AnthropicForbiddenException)
        {
            throw new NonRetriableException("The model provider refused the API key.");
        }
        catch (Exception exception) when (exception is AnthropicBadRequestException or AnthropicNotFoundException)
        {
            throw new NonRetriableException($"The model provider refused the request: {exception.Message}");
        }
    }
}
