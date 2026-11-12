# Agent Framework sample

A [Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/) agent that runs durably in an Inngest
function. It is a travel agent with two tools: `find_flights` runs right away, and `book_flight` waits until a person
approves it. The agent is a plain `ChatClientAgent`. What makes it durable is the chat client under it, built with
`UseInngestFunctionInvocation` and `UseInngestSteps` from
[`Inngest.Extensions.AI`](https://damikun.github.io/inngest-dotnet/guides/agent-framework/):

- every model call and every tool call is a step, so a replay or a retry never calls them twice;
- the wait for the approval is a step too, so the run holds no thread or memory while it waits, and survives restarts
  and deploys.

## How it works

1. `POST /api/trips` sends a `travel/trip.requested` event.
2. `PlanTrip` runs the agent. The model calls `find_flights`, then `book_flight`, which comes back as a
   `ToolApprovalRequestContent` instead of running.
3. The function logs the booking it waits for and waits for a `travel/booking.decided` event.
4. `POST /api/trips/{tripId}/approvals/1` sends that event. The function hands the decision to the agent in the same
   session: an approved booking runs once, as a step, and a rejected one tells the model why.

The steps of a booking that was approved are `chat`, `tool find_flights`, `chat:1`, `approval 1`, `tool book_flight`
and `chat:2`.

## Run it

The agent needs a model that speaks the OpenAI chat API. Put the key in
[user secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets), outside the repository:

```bash
dotnet user-secrets set OpenAI:ApiKey <your key> --project samples/Inngest.AgentFrameworkSample
```

For a local model, set `OpenAI:Endpoint` instead, for example `http://localhost:11434/v1` for Ollama, and
`OpenAI:Model` to a model that can call tools. Then start the app and the dev server, each in its own terminal:

```bash
dotnet run --project samples/Inngest.AgentFrameworkSample
```

```bash
dotnet tool install --global dotnet-inngest
cd samples/Inngest.AgentFrameworkSample && dotnet inngest dev
```

Ask for a trip:

```bash
curl -X POST http://localhost:5294/api/trips -H 'content-type: application/json' \
  -d '{"request":"Book me the cheapest flight from Prague to London on 2026-12-01. I am Jana Novak."}'
```

The app logs the booking that waits, and the run in the dev server at http://localhost:8288 waits at `approval 1`.
Approve it with the `tripId` the first call returned, or reject it with a note, such as `"approved":false,"note":"the
morning flight instead"`:

```bash
curl -X POST http://localhost:5294/api/trips/<tripId>/approvals/1 -H 'content-type: application/json' -d '{"approved":true}'
```

`GET /api/bookings` lists what was booked. Stop the app while the run waits, start it again and approve: the run goes
on where it was, without calling the model again for what it already did.

## Files

- `Program.cs`: the chat client with the two stages, the agent made from it, and the endpoints that send the events.
- `PlanTrip.cs`: the function, with the agent, the wait for each approval and the events.
- `TravelDesk.cs`: the tools, over a few made-up flights, with the bookings kept in memory.

## Notes

- `ChatClientAgent` adds a function invoking client of its own only when its chat client has none. With
  `UseInngestSteps` alone, the model calls would be steps but `book_flight` would run again on every replay.
- The decision event has an id made of the trip and the approval number, so a second decision for the same booking is
  dropped.
- Anyone who knows a trip id can decide for it. A real app would check who is signed in.
