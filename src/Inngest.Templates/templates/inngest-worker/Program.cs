using Inngest;

var builder = Host.CreateApplicationBuilder(args);

// The settings come from the Inngest section of appsettings.json. appsettings.Development.json turns on dev mode, which
// talks to the Inngest dev server on this machine and needs no keys.
builder.Services
    .AddInngest(builder.Configuration.GetSection("Inngest"))
    .AddFunction<SayHello>()
    .AddConnect();

// The worker dials out to Inngest and receives its calls over that connection, so it has no endpoint to expose.
await builder.Build().RunAsync();
