using Aspire.Hosting.Testing;

namespace Inngest.Aspire.Hosting.Tests.Containers;

// The dev server and a project that uses it: the AppHost of the integration's documentation, in its smallest form.
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
    }
}
