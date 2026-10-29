using System.Text.Json.Serialization;
using Inngest;
using Inngest.AotSample;

// Publishes with Native AOT: dotnet publish -c Release -r <rid>. Every type the functions serialize is in AppJson.
var builder = WebApplication.CreateSlimBuilder(args);

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
    .AddFunction<WelcomeMember>()
    .AddFunction<ScoreMember>();

var app = builder.Build();
app.MapInngest();

// Sends a typed event, which WelcomeMember is triggered by: curl -X POST http://localhost:5000/members/Ada
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
    public sealed class WelcomeMember : InngestFunction<MemberJoined, Reply>
    {
        public override async Task<Reply> ExecuteAsync(FunctionContext<MemberJoined> context, CancellationToken cancellationToken)
        {
            var name = context.Event.Data.Name;
            var score = await context.Step.InvokeAsync<ScoreMember, MemberScore>("score", new ScoreRequest(name));
            await context.Step.SendEventAsync("announce", new MemberWelcomed(name, score.Score));
            return new Reply($"Welcome, {name}! Your score is {score.Score}.");
        }
    }

    // Has no trigger; WelcomeMember invokes it by its class.
    [InngestFunction("score-member")]
    public sealed class ScoreMember : InngestFunction<ScoreRequest, MemberScore>
    {
        public override async Task<MemberScore> ExecuteAsync(FunctionContext<ScoreRequest> context, CancellationToken cancellationToken)
        {
            var score = await context.Step.RunAsync("compute", () => context.Event.Data.Name.Length * 10);
            return new MemberScore(context.Event.Data.Name, score);
        }
    }

    [JsonSerializable(typeof(Greeting))]
    [JsonSerializable(typeof(Reply))]
    [JsonSerializable(typeof(MemberJoined))]
    [JsonSerializable(typeof(MemberWelcomed))]
    [JsonSerializable(typeof(ScoreRequest))]
    [JsonSerializable(typeof(MemberScore))]
    internal sealed partial class AppJson : JsonSerializerContext;
}
