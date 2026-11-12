using System.ClientModel;
using Inngest;
using Inngest.AgentFrameworkSample;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;

var builder = WebApplication.CreateBuilder(args);

// Any OpenAI-compatible model: OpenAI itself with OpenAI:ApiKey, or a local one, such as Ollama, with OpenAI:Endpoint.
var openAI = builder.Configuration.GetSection("OpenAI");
var endpoint = openAI["Endpoint"] is { Length: > 0 } url ? new Uri(url) : null;
var key = openAI["ApiKey"] is { Length: > 0 } apiKey ? apiKey
    : endpoint is not null ? "unused"
    : throw new InvalidOperationException("Set OpenAI:ApiKey, or OpenAI:Endpoint for a local model. See the sample's README.");
var model = new OpenAIClient(new ApiKeyCredential(key), new OpenAIClientOptions { Endpoint = endpoint })
    .GetChatClient(openAI["Model"] ?? "gpt-4o-mini")
    .AsIChatClient();

// The pipeline that makes the agent durable: each model call and each tool call is a step of the run it is made in.
// ChatClientAgent finds the function invoking client here and adds no tool loop of its own.
builder.Services
    .AddChatClient(model)
    .UseInngestFunctionInvocation()
    .UseInngestSteps();

builder.Services.AddSingleton<TravelDesk>();
builder.Services.AddSingleton<AIAgent>(services => services.GetRequiredService<IChatClient>().AsAIAgent(
    instructions: TravelDesk.Instructions,
    name: "travel-agent",
    tools: services.GetRequiredService<TravelDesk>().Tools,
    loggerFactory: services.GetRequiredService<ILoggerFactory>(),
    services: services));

builder.Services
    .AddInngest(builder.Configuration.GetSection("Inngest"))
    .AddFunction<PlanTrip>();

var app = builder.Build();

app.MapInngest("/api/inngest");

app.MapPost("/api/trips", async (StartTrip request, IInngestClient inngest, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Request) || request.Request.Length > 2000)
    {
        return Results.BadRequest();
    }

    var tripId = Guid.NewGuid().ToString();
    await inngest.SendAsync(new TripRequested(tripId, request.Request), cancellationToken);
    return Results.Accepted(value: new { tripId });
});

app.MapPost("/api/trips/{tripId:guid}/approvals/{approval:int}", async (Guid tripId, int approval, Decide request, IInngestClient inngest, CancellationToken cancellationToken) =>
{
    if (approval < 1 || request.Note?.Length > 1000)
    {
        return Results.BadRequest();
    }

    // The id makes a second decision for the same approval a duplicate, which Inngest drops.
    var decided = new BookingDecided(tripId.ToString(), approval, request.Approved, request.Note);
    await inngest.SendAsync(new InngestEvent(BookingDecided.EventName, decided) { Id = $"booking-{tripId}-{approval}" }, cancellationToken);
    return Results.Accepted();
});

app.MapGet("/api/bookings", (TravelDesk desk) => desk.Bookings);

app.Run();

internal sealed record StartTrip(string? Request);

internal sealed record Decide(bool Approved, string? Note);
