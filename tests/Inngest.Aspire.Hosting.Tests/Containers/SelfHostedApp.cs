using Aspire.Hosting.Testing;

namespace Inngest.Aspire.Hosting.Tests.Containers;

// A self-hosted server that signs its requests, with keys the integration generates, and a project that has the same keys.
public sealed class SelfHostedApp : AppHostFixture
{
    public InngestServerResource Server { get; private set; } = null!;

    protected override void Configure(IDistributedApplicationTestingBuilder builder)
    {
        var inngest = builder.AddInngestSelfHosted("inngest");
        var shop = builder.AddProject<Projects.TestApp>("shop")
            .WithHttpEndpoint(name: "http")
            .WithHttpHealthCheck("/health")
            .WithReference(inngest);
        inngest.WithApp(shop);
        Server = inngest.Resource;
    }
}
