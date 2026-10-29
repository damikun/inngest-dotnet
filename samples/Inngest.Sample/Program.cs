using Inngest;
using Inngest.Sample;
using Inngest.Sample.Functions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IEmailSender, ConsoleEmailSender>();

builder.Services
    .AddInngest(options => options.AppId = "inngest-dotnet-sample")
    .AddFunction<OnboardUser>(new FunctionOptions
    {
        Id = "onboard-user",
        Name = "Onboard a new user",
        Triggers = [Trigger.Event(Events.UserSignedUp)],
        Retries = 3,
        Concurrency = [new ConcurrencyLimit(10)],
        CancelOn = [new CancelOn(Events.UserDeleted) { Match = "data.userId" }],
    })
    .AddFunction<SummarizeAccounts>(new FunctionOptions
    {
        Id = "summarize-accounts",
        Triggers = [Trigger.Event(Events.SummaryRequested), Trigger.Cron("0 6 * * *", timezone: "Europe/Prague")],
    })
    .AddFunction<ScoreAccount>(new FunctionOptions
    {
        Id = "score-account",
        Triggers = [],
    })
    .AddFunction(new FunctionOptions { Id = "hello-world", Triggers = [Trigger.Event("demo/hello")] },
        async (context, cancellationToken) =>
        {
            var greeting = await context.Step.RunAsync("greet", () => $"Hello, {context.Event.Data.GetProperty("name").GetString()}!");
            return new { greeting };
        });

var app = builder.Build();

app.MapInngest("/api/inngest");

// Sends an event the way application code would, outside of any function.
app.MapPost("/signup", async (SignupRequest request, IInngestClient inngest, CancellationToken cancellationToken) =>
{
    var userId = Guid.NewGuid().ToString("N");
    var sent = await inngest.SendAsync(new InngestEvent(Events.UserSignedUp, new UserSignedUp(userId, request.Email)), cancellationToken);
    return Results.Accepted(value: new { userId, eventId = sent.Ids[0] });
});

app.Run();

internal sealed record SignupRequest(string Email);
