using System.Security.Claims;
using System.Text.Json.Serialization;
using Inngest;
using Inngest.AotSample;
using Microsoft.AspNetCore.DataProtection;

// Publishes with Native AOT: dotnet publish -c Release -r <rid>. Every type the functions serialize is in AppJson, and
// AddInngestFunctions(), which the generator writes, registers the function classes below.
var builder = WebApplication.CreateSlimBuilder(args);

// The user behind an event is protected with Data Protection. An app that sends users and an app that restores them share
// the key ring and the application name; this one is both, with its keys in a folder next to it, unencrypted, which a
// real app avoids with ProtectKeysWith....
builder.Services
    .AddDataProtection()
    .SetApplicationName("inngest-dotnet-aot-sample")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "keys")));

// The app id comes from the Inngest section of appsettings.json, which the SDK reads key by key, without the
// reflection-based configuration binder.
builder.Services
    .AddInngest(builder.Configuration.GetSection("Inngest"),
        options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJson.Default))
    .AddFunction<Greeting>(new FunctionOptions { Id = "greet", Triggers = [Trigger.Event("aot/greet")] },
        async (context, cancellationToken) =>
        {
            var reply = await context.Step.RunAsync("compose", () => new Reply($"Hello, {context.Event.Data.Name}!"));
            await context.Step.SleepAsync("pause", TimeSpan.FromSeconds(1));
            await context.Step.RunAsync("audit", () => context.Logger.LogInformation("Greeted {Name}", context.Event.Data.Name));
            return reply;
        })
    .AddInngestFunctions()
    .AddUserContext(options => options.Claims = ["sub", "name"])
    .AddUserContextFromHttpContext();

var app = builder.Build();

// The sample has no sign-in, so a header names the member for this demonstration: curl -H "X-Member: Ada" -X POST ...
app.Use((context, next) =>
{
    if (context.Request.Headers.TryGetValue("X-Member", out var member) && member.ToString() is { Length: > 0 } name)
    {
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", name), new Claim("name", name)], "Demo"));
    }

    return next(context);
});
app.MapInngest();

// Receives Stripe's webhooks as events named stripe/<type>, after checking their signature with the signing secret of the
// webhook endpoint. The secret in appsettings.json is a placeholder. The endpoint is part of the Native AOT publish, so CI
// covers the verifiers and the event mapping with the rest of the app.
app.MapInngestWebhook(
    "/webhooks/stripe",
    WebhookProvider.Stripe(builder.Configuration["Stripe:WebhookSecret"] ?? throw new InvalidOperationException("Set Stripe:WebhookSecret to the signing secret of the webhook endpoint.")));

// Sends a typed event, which WelcomeMember is triggered by: curl -X POST http://localhost:5000/members/Ada
// The signed-in member travels with the event, and WelcomeMember reads it as ICurrentUser.
app.MapPost("/members/{name}", async (string name, IInngestClient inngest, CancellationToken cancellationToken) =>
{
    var sent = await inngest.SendAsync(new MemberJoined(name), cancellationToken);
    return Results.Text(sent.Ids[0], statusCode: StatusCodes.Status202Accepted);
});

app.Run();

namespace Inngest.AotSample
{
    public sealed record Greeting(string Name);

    public sealed record Reply(string Text);

    public sealed record MemberJoined(string Name) : IInngestEventData
    {
        public static string EventName => "aot/member.joined";
    }

    public sealed record MemberLeft(string Name) : IInngestEventData
    {
        public static string EventName => "aot/member.left";
    }

    public sealed record MemberWelcomed(string Name, int Score) : IInngestEventData
    {
        public static string EventName => "aot/member.welcomed";
    }

    public sealed record ScoreRequest(string Name);

    public sealed record MemberScore(string Name, int Score);

    // Configured by its attributes, generic ones included, which AddFunction reads at startup under Native AOT too.
    [InngestFunction("welcome-member", Name = "Welcome a member", Retries = 2)]
    [EventTrigger<MemberJoined>]
    [Concurrency(5)]
    [CancelOn<MemberLeft>(Match = "data.name", Timeout = "1h")]
    public sealed class WelcomeMember(ICurrentUser user) : InngestFunction<MemberJoined, Reply>
    {
        public override async Task<Reply> ExecuteAsync(FunctionContext<MemberJoined> context, CancellationToken cancellationToken)
        {
            var name = context.Event.Data.Name;
            var score = await context.Step.InvokeAsync<ScoreMember, MemberScore>("score", new ScoreRequest(name));
            await context.Step.SendEventAsync("announce", new MemberWelcomed(name, score.Score));
            context.Logger.LogInformation("Welcomed {Name}, who was signed in as {User}", name, user.IsAuthenticated ? user.Principal.Identity?.Name : "nobody");
            return new Reply($"Welcome, {name}! Your score is {score.Score}.");
        }
    }

    // Has no trigger; WelcomeMember invokes it by its class.
    [InngestFunction("score-member")]
    [OnePerMember]
    public sealed class ScoreMember : InngestFunction<ScoreRequest, MemberScore>
    {
        public override async Task<MemberScore> ExecuteAsync(FunctionContext<ScoreRequest> context, CancellationToken cancellationToken)
        {
            var score = await context.Step.RunAsync("compute", () => context.Event.Data.Name.Length * 10);
            return new MemberScore(context.Event.Data.Name, score);
        }
    }

    // An attribute of the app's own, which AddFunction applies like the built-in ones: one step per member at a time.
    public sealed class OnePerMemberAttribute : FunctionOptionsAttribute
    {
        protected override FunctionOptions Configure(FunctionOptions options) =>
            options with { Concurrency = [.. options.Concurrency ?? [], new ConcurrencyLimit(1) { Key = "event.data.name" }] };
    }

    [JsonSerializable(typeof(Greeting))]
    [JsonSerializable(typeof(Reply))]
    [JsonSerializable(typeof(MemberJoined))]
    [JsonSerializable(typeof(MemberWelcomed))]
    [JsonSerializable(typeof(ScoreRequest))]
    [JsonSerializable(typeof(MemberScore))]
    internal sealed partial class AppJson : JsonSerializerContext;
}
