using Inngest;

var builder = WebApplication.CreateBuilder(args);

// The settings come from the Inngest section of appsettings.json. appsettings.Development.json turns on dev mode, which
// talks to the Inngest dev server on this machine and needs no keys.
builder.Services
    .AddInngest(builder.Configuration.GetSection("Inngest"))
    .AddFunction<SayHello>();

var app = builder.Build();

// Inngest calls this endpoint to sync the app's functions and to run their steps.
app.MapInngest();

// Sends the event that starts SayHello: curl -X POST http://localhost:5280/hello/Ada
app.MapPost("/hello/{name}", async (string name, IInngestClient inngest, CancellationToken cancellationToken) =>
{
    var sent = await inngest.SendAsync(new HelloRequested(name), cancellationToken);
    return Results.Accepted(value: new { eventId = sent.Ids[0] });
});

app.Run();
