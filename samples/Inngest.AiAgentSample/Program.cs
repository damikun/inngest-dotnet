using Inngest;
using Inngest.AiAgentSample;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ApiKeys>();
builder.Services.AddHttpClient<AgentTools>();
builder.Services.Configure<AgentSettings>(builder.Configuration.GetSection("Agent"));

builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(1);
    options.Cookie.Name = "agent-session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.IsEssential = true;
});

builder.Services
    .AddInngest(builder.Configuration.GetSection("Inngest"))
    .AddFunction<RunAgent>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseSession();
app.MapInngest("/api/inngest");

const string SessionKey = "api-key";
const string SessionProvider = "provider";

app.MapGet("/api/key", (HttpContext context) =>
    context.Session.GetString(SessionKey) is { } key && context.Session.GetString(SessionProvider) is { } provider
        ? Results.Ok(SavedKey.Of(provider, key))
        : Results.NotFound());

app.MapPut("/api/key", (SaveKey request, HttpContext context) =>
{
    if (!Providers.IsKnown(request.Provider) || string.IsNullOrWhiteSpace(request.ApiKey) || request.ApiKey.Length > 512)
    {
        return Results.BadRequest();
    }

    var apiKey = request.ApiKey.Trim();
    context.Session.SetString(SessionProvider, request.Provider!);
    context.Session.SetString(SessionKey, apiKey);
    return Results.Ok(SavedKey.Of(request.Provider!, apiKey));
});

app.MapDelete("/api/key", (HttpContext context) =>
{
    context.Session.Clear();
    return Results.NoContent();
});

app.MapPost("/api/tasks", async (StartTask request, HttpContext context, ApiKeys keys, IInngestClient inngest, CancellationToken cancellationToken) =>
{
    if (!Guid.TryParse(request.TaskId, out var id) || string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > 4000)
    {
        return Results.BadRequest();
    }

    if (context.Session.GetString(SessionKey) is not { } apiKey || context.Session.GetString(SessionProvider) is not { } provider)
    {
        return Results.Unauthorized();
    }

    var taskId = id.ToString();
    if (!keys.TryAdd(taskId, apiKey))
    {
        return Results.Conflict();
    }

    try
    {
        await inngest.SendAsync(new AgentTaskRequested(taskId, request.Prompt, provider), cancellationToken);
    }
    catch
    {
        keys.Remove(taskId);
        throw;
    }

    return Results.Accepted();
});

app.MapGet("/api/dev-server", (IConfiguration configuration) =>
{
    var devServer = Uri.TryCreate(configuration["INNGEST_DEV"], UriKind.Absolute, out var url) && url.Scheme.StartsWith("http", StringComparison.Ordinal)
        ? url.GetLeftPart(UriPartial.Authority)
        : "http://localhost:8288";
    return Results.Ok(new { url = devServer });
});

app.MapInngestRealtime("/api/tasks/{taskId:guid}/updates", context =>
{
    var taskId = Guid.Parse((string)context.Request.RouteValues["taskId"]!).ToString();
    return ValueTask.FromResult<RealtimeSubscription?>(new(AgentTopics.Channel(taskId), ["status", "answer"]));
});

app.Run();

internal sealed record SaveKey(string? Provider, string? ApiKey);

internal sealed record SavedKey(string Provider, string EndsWith)
{
    public static SavedKey Of(string provider, string key) => new(provider, key.Length > 8 ? key[^4..] : "");
}

internal sealed record StartTask(string? TaskId, string? Prompt);
