using Inngest;

// The app that the tests of the Aspire integration run as a project of the AppHost. It has one function with two steps,
// so that the server has to call back into the app more than once, and a health endpoint that works in both modes. It
// serves its function over HTTP, or, when TEST_WORKER is true, runs it as a Connect worker with an app id and an event of
// its own, so that one AppHost can have both without the two answering the same event.
var builder = WebApplication.CreateBuilder(args);
var worker = builder.Configuration["TEST_WORKER"] == "true";
var eventName = worker ? "aspire/worker.hello" : "aspire/hello";

var inngest = builder.Services
    .AddInngest(options => options.AppId = worker ? "aspire-test-worker" : "aspire-test-app")
    .AddFunction(
        new FunctionOptions { Id = "hello", Triggers = [Trigger.Event(eventName)] },
        async (context, cancellationToken) =>
        {
            var first = await context.Step.RunAsync("first", () => "hello");
            var second = await context.Step.RunAsync("second", () => $"{first} from aspire");
            return new { greeting = second };
        });

if (worker)
{
    inngest.AddConnect();
}

var app = builder.Build();

app.MapGet("/health", () => "ok");

if (!worker)
{
    app.MapInngest("/api/inngest");
}

// Sends the event the way application code does, so that the tests see where the settings from the AppHost lead.
app.MapPost("/send", async (IInngestClient client, CancellationToken cancellationToken) =>
{
    var sent = await client.SendAsync(new InngestEvent(eventName), cancellationToken);
    return Results.Ok(new { id = sent.Ids.Single() });
});

app.Run();
