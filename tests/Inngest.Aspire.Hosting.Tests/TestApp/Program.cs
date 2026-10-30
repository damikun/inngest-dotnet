using Inngest;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddInngest(options => options.AppId = "aspire-test-app")
    .AddFunction(
        new FunctionOptions { Id = "hello", Triggers = [Trigger.Event("aspire/hello")] },
        (context, cancellationToken) => Task.FromResult<object?>(new { greeting = "hello" }));

var app = builder.Build();

app.MapInngest("/api/inngest");

app.Run();
