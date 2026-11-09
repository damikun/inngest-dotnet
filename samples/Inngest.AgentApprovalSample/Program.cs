using Inngest;
using Inngest.AgentApprovalSample;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AgentSettings>(builder.Configuration.GetSection("Agent"));
builder.Services.AddSingleton<Shop>();
builder.Services.AddSingleton<SupportTools>();
builder.Services.AddSingleton<ModelClient>();

builder.Services
    .AddInngest(builder.Configuration.GetSection("Inngest"))
    .AddFunction<RunSupportAgent>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapInngest("/api/inngest");

app.MapGet("/api/setup", (ModelClient model, IConfiguration configuration) =>
{
    var devServer = Uri.TryCreate(configuration["INNGEST_DEV"], UriKind.Absolute, out var url) && url.Scheme.StartsWith("http", StringComparison.Ordinal)
        ? url.GetLeftPart(UriPartial.Authority)
        : "http://localhost:8288";
    return Results.Ok(new { provider = model.Provider, model = model.Model, devServer });
});

app.MapGet("/api/shop", (Shop shop) => Results.Ok(new { orders = shop.Orders, outbox = shop.Outbox }));

app.MapPost("/api/tasks", async (StartTask request, ModelClient model, IInngestClient inngest, CancellationToken cancellationToken) =>
{
    var history = request.History ?? [];
    if (!Guid.TryParse(request.TaskId, out var id)
        || string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > 4000
        || history.Count > 20 || history.Any(turn => turn.Role is not ("user" or "assistant") || turn.Text is not { Length: > 0 and <= 8000 }))
    {
        return Results.BadRequest();
    }

    if (model.Provider is null)
    {
        return Results.Problem("No model is set up.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    await inngest.SendAsync(new SupportTaskRequested(id.ToString(), request.Prompt, history), cancellationToken);
    return Results.Accepted();
});

app.MapPost("/api/tasks/{taskId:guid}/approvals/{approval:int}", async (Guid taskId, int approval, Decide request, IInngestClient inngest, CancellationToken cancellationToken) =>
{
    if (approval < 1 || request.Note?.Length > 1000)
    {
        return Results.BadRequest();
    }

    var decided = new ApprovalDecided(taskId.ToString(), approval, request.Approved, string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim());
    await inngest.SendAsync(new InngestEvent(ApprovalDecided.EventName, decided) { Id = $"approval-{taskId}-{approval}" }, cancellationToken);
    return Results.Accepted();
});

app.MapInngestRealtime("/api/tasks/{taskId:guid}/updates", context =>
{
    var taskId = Guid.Parse((string)context.Request.RouteValues["taskId"]!).ToString();
    return ValueTask.FromResult<RealtimeSubscription?>(new(AgentTopics.Channel(taskId), ["status", "answer"]));
});

app.Run();

internal sealed record StartTask(string? TaskId, string? Prompt, List<Turn>? History);

internal sealed record Decide(bool Approved, string? Note);
