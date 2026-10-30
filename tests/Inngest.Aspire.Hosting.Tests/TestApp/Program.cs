using Inngest;

// The app that the tests of the Aspire integration run as a project of the AppHost: one function with two steps, so that
// the server has to call back into the app more than once, and a health endpoint that works in both modes.
var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddInngest(options => options.AppId = "aspire-test-app")
    .AddFunction(
        new FunctionOptions { Id = "hello", Triggers = [Trigger.Event("aspire/hello")] },
        async (context, cancellationToken) =>
        {
            var first = await context.Step.RunAsync("first", () => "hello");
            var second = await context.Step.RunAsync("second", () => $"{first} from aspire");
            return new { greeting = second };
        });

var app = builder.Build();

app.MapGet("/health", () => "ok");
app.MapInngest("/api/inngest");

// Sends the event the way application code does, so that the tests see where the settings from the AppHost lead.
app.MapPost("/send", async (IInngestClient inngest, CancellationToken cancellationToken) =>
{
    var sent = await inngest.SendAsync(new InngestEvent("aspire/hello"), cancellationToken);
    return Results.Ok(new { id = sent.Ids.Single() });
});

app.Run();
