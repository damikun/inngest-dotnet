using System.Text.Json;

namespace Inngest.AgentApprovalSample;

public sealed record SupportTaskRequested(string TaskId, string Prompt, IReadOnlyList<Turn> History) : IInngestEventData
{
    public static string EventName => "support/task.requested";
}

public sealed record Turn(string Role, string Text);

public sealed record ApprovalDecided(string TaskId, int Approval, bool Approved, string? Note) : IInngestEventData
{
    public static string EventName => "support/approval.decided";
}

public sealed record AgentUpdate(string Kind, string? Text = null, string? Tool = null, int? Approval = null, JsonElement? Arguments = null);

public static class AgentTopics
{
    public static string Channel(string taskId) => $"support:{taskId}";

    public static RealtimeTopic<AgentUpdate> Status(string taskId) => new(Channel(taskId), "status");

    public static RealtimeTopic Answer(string taskId) => new(Channel(taskId), "answer");
}
