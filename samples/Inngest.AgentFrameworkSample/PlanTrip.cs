using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Inngest.AgentFrameworkSample;

public sealed record TripRequested(string TripId, string Request) : IInngestEventData
{
    public static string EventName => "travel/trip.requested";
}

public sealed record BookingDecided(string TripId, int Approval, bool Approved, string? Note) : IInngestEventData
{
    public static string EventName => "travel/booking.decided";
}

// An Agent Framework agent in an Inngest function. The agent is the one from Program.cs, unchanged; what makes it durable
// is the chat client under it. The run is replayed from the start for every step, and each replay makes a new session
// and runs the agent again: the model calls and tool calls that already happened return their saved results, so the
// session fills up the same way and nothing is called twice.
[InngestFunction("plan-trip", Name = "Plan a trip with an Agent Framework agent", Retries = 2)]
[EventTrigger<TripRequested>]
public sealed class PlanTrip(AIAgent agent) : InngestFunction<TripRequested>
{
    public override async Task<object?> ExecuteAsync(FunctionContext<TripRequested> context, CancellationToken cancellationToken)
    {
        var trip = context.Event.Data;
        var session = await agent.CreateSessionAsync(cancellationToken);
        var response = await agent.RunAsync(trip.Request, session, cancellationToken: cancellationToken);

        // book_flight needs approval: the agent stops and returns a request for it instead of calling the tool.
        var approvals = 0;
        while (response.Messages.SelectMany(message => message.Contents).OfType<ToolApprovalRequestContent>().ToList() is { Count: > 0 } requests)
        {
            List<AIContent> answers = [];
            foreach (var request in requests)
            {
                var approval = ++approvals;
                var call = (FunctionCallContent)request.ToolCall;

                // context.Logger writes once per run, not again on each replay.
                context.Logger.LogInformation(
                    "Trip {TripId} waits for approval {Approval}: {Tool}({Arguments})",
                    trip.TripId, approval, call.Name, string.Join(", ", call.Arguments?.Select(pair => $"{pair.Key}: {pair.Value}") ?? []));

                // The wait is a step: the run holds no thread or memory while it waits, and survives restarts and deploys.
                var decision = await context.Step.WaitForEventAsync<BookingDecided>(
                    $"approval {approval}",
                    TimeSpan.FromDays(1),
                    @if: $"async.data.tripId == event.data.tripId && async.data.approval == {approval}");

                answers.Add(decision is { Data: var decided }
                    ? request.CreateResponse(decided.Approved, decided.Note)
                    : request.CreateResponse(approved: false, "No one answered in time."));
            }

            // The approved tools run now, each as a step, and the model hears which were rejected and why.
            response = await agent.RunAsync(new ChatMessage(ChatRole.User, answers), session, cancellationToken: cancellationToken);
        }

        return new { answer = response.Text };
    }
}
