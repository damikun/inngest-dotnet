using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Inngest.AgentApprovalSample;

[InngestFunction("support-agent", Name = "Run a support task", Retries = 2)]
[EventTrigger<SupportTaskRequested>]
public sealed class RunSupportAgent(ModelClient model, SupportTools tools, IOptions<AgentSettings> settings)
    : InngestFunction<SupportTaskRequested>, IFailureHandler
{
    private const string Instructions = """
        You are the support agent of Brew Supply, a small shop for coffee gear. Look an order up with find_order before you
        act on it. Refunds and emails wait until a person approves them. When a person rejects one with a note, do what the
        note asks, which can mean calling the tool again with changes. Reply to the person briefly.
        """;

    public override async Task<object?> ExecuteAsync(FunctionContext<SupportTaskRequested> context, CancellationToken cancellationToken)
    {
        var task = context.Event.Data;
        var status = AgentTopics.Status(task.TaskId);

        using var chat = model.Create();
        var options = new ChatOptions { Instructions = Instructions, Tools = tools.For(status) }
            .WithInngestRealtimeStream(AgentTopics.Answer(task.TaskId));

        List<ChatMessage> messages =
        [
            .. task.History.Select(turn => new ChatMessage(turn.Role == "assistant" ? ChatRole.Assistant : ChatRole.User, turn.Text)),
            new(ChatRole.User, task.Prompt),
        ];

        var approvals = 0;
        while (true)
        {
            var response = await chat
                .GetStreamingResponseAsync(messages, options, cancellationToken)
                .ToChatResponseAsync(cancellationToken);
            messages.AddRange(response.Messages);

            var requests = response.Messages.SelectMany(message => message.Contents).OfType<ToolApprovalRequestContent>().ToList();
            if (requests.Count == 0)
            {
                await context.Step.PublishAsync("publish-answer", status, new AgentUpdate("answer", response.Text));
                return new { answer = response.Text };
            }

            var numbered = requests.Select(request => (Request: request, Number: request.RequiresConfirmation ? ++approvals : 0)).ToList();
            var answers = await Task.WhenAll(numbered.Select(item => item.Number == 0
                ? Task.FromResult(item.Request.CreateResponse(approved: true))
                : AskAsync(context, status, item.Request, item.Number)));
            messages.Add(new ChatMessage(ChatRole.User, [.. answers]));
        }
    }

    public async Task OnFailureAsync(FunctionContext<FunctionFailure> context, CancellationToken cancellationToken)
    {
        var taskId = context.Event.Data.Event.GetProperty("data").GetProperty("taskId").GetString()!;
        await context.Step.PublishAsync("publish-error", AgentTopics.Status(taskId), new AgentUpdate("error", context.Event.Data.Error.Message));
    }

    private async Task<ToolApprovalResponseContent> AskAsync(
        FunctionContext<SupportTaskRequested> context,
        RealtimeTopic<AgentUpdate> status,
        ToolApprovalRequestContent request,
        int approval)
    {
        var call = (FunctionCallContent)request.ToolCall;
        var arguments = JsonSerializer.SerializeToElement(call.Arguments, AIJsonUtilities.DefaultOptions);
        await context.Step.PublishAsync($"approval {approval} requested", status,
            new AgentUpdate("approval", Tool: call.Name, Approval: approval, Arguments: arguments));

        var decision = await context.Step.WaitForEventAsync<ApprovalDecided>(
            $"approval {approval}",
            settings.Value.ApprovalTimeout,
            @if: $"async.data.taskId == event.data.taskId && async.data.approval == {approval}");

        var kind = decision?.Data switch
        {
            null => "expired",
            { Approved: true } => "approved",
            _ => "rejected",
        };
        await context.Step.PublishAsync($"approval {approval} {kind}", status, new AgentUpdate(kind, decision?.Data.Note, call.Name, approval));

        return decision?.Data is { } answer
            ? request.CreateResponse(answer.Approved, answer.Note)
            : request.CreateResponse(approved: false, "No one answered in time.");
    }
}
