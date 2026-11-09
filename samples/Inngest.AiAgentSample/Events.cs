namespace Inngest.AiAgentSample;

public sealed record AgentTaskRequested(string TaskId, string Prompt, string Provider) : IInngestEventData
{
    public static string EventName => "agent/task.requested";
}

public sealed record AgentUpdate(string Kind, string Text, string? Tool = null);

public static class AgentTopics
{
    public static string Channel(string taskId) => $"agent:{taskId}";

    public static RealtimeTopic<AgentUpdate> Status(string taskId) => new(Channel(taskId), "status");

    public static RealtimeTopic Answer(string taskId) => new(Channel(taskId), "answer");
}

public static class Providers
{
    public const string OpenAI = "openai";
    public const string Anthropic = "anthropic";

    public static bool IsKnown(string? provider) => provider is OpenAI or Anthropic;
}

public sealed class AgentSettings
{
    public string OpenAIModel { get; set; } = "gpt-4o-mini";

    public string AnthropicModel { get; set; } = "claude-opus-5-5";

    public Uri? OpenAIEndpoint { get; set; }
}
