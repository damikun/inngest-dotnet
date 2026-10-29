using Inngest;
using Inngest.Sample;
using Inngest.Sample.Functions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IEmailSender, ConsoleEmailSender>();

// The settings come from the Inngest section of appsettings.json, and INNGEST_* variables win over it. The function
// classes carry their id, triggers and limits as attributes; hello-world is a delegate with its options in code, the
// other way to define a function.
builder.Services
    .AddInngest(builder.Configuration.GetSection("Inngest"))
    .AddFunction<OnboardUser>()
    .AddFunction<SummarizeAccounts>()
    .AddFunction<ScoreAccount>()
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
    var sent = await inngest.SendAsync(new UserSignedUp(userId, request.Email), cancellationToken);
    return Results.Accepted(value: new { userId, eventId = sent.Ids[0] });
});

app.Run();

internal sealed record SignupRequest(string Email);
