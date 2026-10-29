using System.Text.Json.Serialization;
using Inngest;
using Inngest.AotSample;

// Publishes with Native AOT: dotnet publish -c Release -r <rid>. Every type the functions serialize is in AppJson.
var builder = WebApplication.CreateSlimBuilder(args);

builder.Services
    .AddInngest(options =>
    {
        options.AppId = "inngest-dotnet-aot-sample";
        options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJson.Default);
    })
    .AddFunction<Greeting>(new FunctionOptions { Id = "greet", Triggers = [Trigger.Event("aot/greet")] },
        async (context, cancellationToken) =>
        {
            var reply = await context.Step.RunAsync("compose", () => new Reply($"Hello, {context.Event.Data.Name}!"));
            await context.Step.SleepAsync("pause", TimeSpan.FromSeconds(1));
            await context.Step.RunAsync("audit", () => context.Logger.LogInformation("Greeted {Name}", context.Event.Data.Name));
            return reply;
        });

var app = builder.Build();
app.MapInngest();
app.Run();

namespace Inngest.AotSample
{
    public sealed record Greeting(string Name);

    public sealed record Reply(string Text);

    [JsonSerializable(typeof(Greeting))]
    [JsonSerializable(typeof(Reply))]
    internal sealed partial class AppJson : JsonSerializerContext;
}
