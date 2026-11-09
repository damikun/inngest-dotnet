using System.ClientModel;
using Anthropic;
using Anthropic.Exceptions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;

namespace Inngest.AiAgentSample;

[InngestFunction("run-agent", Name = "Run an agent task", Retries = 2)]
[EventTrigger<AgentTaskRequested>]
public sealed class RunAgent(ApiKeys keys, AgentTools tools, IOptions<AgentSettings> settings, IServiceProvider services)
    : InngestFunction<AgentTaskRequested>, IFailureHandler
{
    private const string Instructions =
        "Answer briefly. Use the tools when the task needs the current time or the weather somewhere.";

    public override async Task<object?> ExecuteAsync(FunctionContext<AgentTaskRequested> context, CancellationToken cancellationToken)
    {
        var task = context.Event.Data;
        var status = AgentTopics.Status(task.TaskId);

        var apiKey = keys.Get(task.TaskId) ?? throw new NonRetriableException(
            "The API key of this task is gone, because the app restarted or the task is older than 30 minutes. Start it again.");

        using var chat = CreateChatClient(task.Provider, apiKey);
        var options = new ChatOptions { Instructions = Instructions, Tools = tools.For(status) }
            .WithInngestRealtimeStream(AgentTopics.Answer(task.TaskId));

        var response = await chat
            .GetStreamingResponseAsync(task.Prompt, options, cancellationToken)
            .ToChatResponseAsync(cancellationToken);

        await context.Step.PublishAsync("publish-answer", status, new AgentUpdate("answer", response.Text));
        keys.Remove(task.TaskId);
        return new { answer = response.Text };
    }

    public async Task OnFailureAsync(FunctionContext<FunctionFailure> context, CancellationToken cancellationToken)
    {
        var taskId = context.Event.Data.Event.GetProperty("data").GetProperty("taskId").GetString()!;
        keys.Remove(taskId);
        await context.Step.PublishAsync("publish-error", AgentTopics.Status(taskId), new AgentUpdate("error", context.Event.Data.Error.Message));
    }

    private IChatClient CreateChatClient(string provider, string apiKey)
    {
        var model = provider switch
        {
            Providers.Anthropic => new AnthropicClient { ApiKey = apiKey }.AsIChatClient(settings.Value.AnthropicModel, defaultMaxOutputTokens: 16000),
            Providers.OpenAI => new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAIClientOptions { Endpoint = settings.Value.OpenAIEndpoint })
                .GetChatClient(settings.Value.OpenAIModel)
                .AsIChatClient(),
            _ => throw new NonRetriableException($"Unknown model provider '{provider}'."),
        };

        return model
            .AsBuilder()
            .UseInngestFunctionInvocation()
            .UseInngestSteps()
            .Use(FailOnRefusalAsync)
            .Build(services);
    }

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
