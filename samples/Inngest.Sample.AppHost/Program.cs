// The dev server runs as a container, and the sample gets the settings that point its SDK at it. The server syncs the
// sample at /api/inngest, which is where the sample maps the endpoint, whichever port Aspire gives it.
var builder = DistributedApplication.CreateBuilder(args);

var inngest = builder.AddInngest("inngest");

var sample = builder.AddProject<Projects.Inngest_Sample>("sample")
    .WithReference(inngest)
    .WaitFor(inngest);

inngest.WithApp(sample);

builder.Build().Run();
