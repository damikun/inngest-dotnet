using Aspire.Hosting.Testing;

namespace Inngest.Aspire.Hosting.Tests.Containers;

// The dev server, a project that it syncs and a Connect worker: the AppHost of the integration's documentation, in its
// smallest form.
public sealed class DevServerApp : AppHostFixture
{
    protected override void Configure(IDistributedApplicationTestingBuilder builder)
    {
        var inngest = builder.AddInngest("inngest");
        var shop = builder.AddProject<Projects.TestApp>("shop")
            .WithHttpEndpoint(name: "http")
            .WithHttpHealthCheck("/health")
            .WithReference(inngest);
        inngest.WithApp(shop);

        // A worker is not registered with the server. It dials the gateway of the server it references.
        builder.AddProject<Projects.TestApp>("worker")
            .WithHttpEndpoint(name: "http")
            .WithHttpHealthCheck("/health")
            .WithEnvironment("TEST_WORKER", "true")
            .WithReference(inngest);
    }
}
