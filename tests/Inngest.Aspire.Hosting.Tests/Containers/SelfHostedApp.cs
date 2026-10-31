using Aspire.Hosting.Testing;

namespace Inngest.Aspire.Hosting.Tests.Containers;

// A self-hosted server that signs its requests, with keys the integration generates, a project that it syncs and a
// Connect worker, which have the same keys.
public sealed class SelfHostedApp : AppHostFixture
{
    public InngestServerResource Server { get; private set; } = null!;

    protected override void Configure(IDistributedApplicationTestingBuilder builder)
    {
        // Nothing waits for anything here: the server and the projects start together, and the server finds the project
        // when it answers.
        var inngest = builder.AddInngestSelfHosted("inngest");
        var shop = builder.AddProject<Projects.TestApp>("shop")
            .WithHttpEndpoint(name: "http")
            .WithHttpHealthCheck("/health")
            .WithReference(inngest);
        inngest.WithApp(shop);
        builder.AddProject<Projects.TestApp>("worker")
            .WithHttpEndpoint(name: "http")
            .WithHttpHealthCheck("/health")
            .WithEnvironment("TEST_WORKER", "true")
            .WithReference(inngest);
        Server = inngest.Resource;
    }
}
