namespace Inngest.Aspire.Hosting.Tests.AppModel;

// What a resource that references the server reads to find it, resolved for the resource that gets it.
public sealed class WithReferenceTests
{
    private const string SigningKey = "0a1b2c3d4e5f60718293a4b5c6d7e8f9";

    private static IResourceBuilder<InngestServerResource> AddDevServer(IDistributedApplicationBuilder builder)
    {
        var inngest = builder.AddInngest("inngest");
        Model.Allocate(inngest.Resource, "http", 18288, "inngest.dev.internal", 8288);
        Model.Allocate(inngest.Resource, "connect", 18289, "inngest.dev.internal", 8289);
        return inngest;
    }

    private static IResourceBuilder<InngestServerResource> AddSelfHostedServer(IDistributedApplicationBuilder builder)
    {
        var inngest = builder.AddInngestSelfHosted("inngest", Model.Secret(builder, "signing", SigningKey), Model.Secret(builder, "events", "event-key-1"));
        Model.Allocate(inngest.Resource, "http", 18288, "inngest.dev.internal", 8288);
        Model.Allocate(inngest.Resource, "connect", 18289, "inngest.dev.internal", 8289);
        return inngest;
    }

    [Fact]
    public async Task A_project_on_the_host_is_pointed_at_the_dev_server_through_the_port_on_the_host()
    {
        var builder = Model.CreateBuilder();
        var inngest = AddDevServer(builder);

        var shop = builder.AddProject<Projects.TestApp>("shop").WithReference(inngest);

        var environment = await Model.EnvironmentAsync(shop.Resource);
        environment["INNGEST_DEV"].ShouldBe("http://localhost:18288");
        environment["INNGEST_BASE_URL"].ShouldBe("http://localhost:18288");
        environment["INNGEST_CONNECT_GATEWAY_URL"].ShouldBe("ws://localhost:18289/v0/connect");
        environment.Keys.Where(key => key.StartsWith("INNGEST_", StringComparison.Ordinal)).Order()
            .ShouldBe(["INNGEST_BASE_URL", "INNGEST_CONNECT_GATEWAY_URL", "INNGEST_DEV"]);
    }

    [Fact]
    public async Task A_container_is_pointed_at_the_dev_server_through_the_container_network()
    {
        var builder = Model.CreateBuilder();
        var inngest = AddDevServer(builder);

        var worker = builder.AddContainer("worker", "example/worker").WithReference(inngest);

        var environment = await Model.EnvironmentAsync(worker.Resource);
        environment["INNGEST_DEV"].ShouldBe("http://inngest.dev.internal:8288");
        environment["INNGEST_BASE_URL"].ShouldBe("http://inngest.dev.internal:8288");
        environment["INNGEST_CONNECT_GATEWAY_URL"].ShouldBe("ws://inngest.dev.internal:8289/v0/connect");
    }

    [Fact]
    public async Task A_project_is_given_the_keys_of_a_self_hosted_server_and_stays_out_of_dev_mode()
    {
        var builder = Model.CreateBuilder();
        var inngest = AddSelfHostedServer(builder);

        var shop = builder.AddProject<Projects.TestApp>("shop").WithReference(inngest);

        // INNGEST_DEV=0 keeps an INNGEST_DEV in the environment of the AppHost, which the project inherits, from turning
        // off the checks of signatures.
        var environment = await Model.EnvironmentAsync(shop.Resource);
        environment["INNGEST_DEV"].ShouldBe("0");
        environment["INNGEST_BASE_URL"].ShouldBe("http://localhost:18288");
        environment["INNGEST_SIGNING_KEY"].ShouldBe(SigningKey);
        environment["INNGEST_EVENT_KEY"].ShouldBe("event-key-1");
        environment["INNGEST_CONNECT_GATEWAY_URL"].ShouldBe("ws://localhost:18289/v0/connect");
    }

    [Fact]
    public async Task The_server_and_the_app_share_the_keys()
    {
        var builder = Model.CreateBuilder();
        var inngest = builder.AddInngestSelfHosted("inngest");
        Model.Allocate(inngest.Resource, "http", 18288, "inngest.dev.internal", 8288);
        Model.Allocate(inngest.Resource, "connect", 18289, "inngest.dev.internal", 8289);

        var shop = builder.AddProject<Projects.TestApp>("shop").WithReference(inngest);

        var server = await Model.EnvironmentAsync(inngest.Resource);
        var app = await Model.EnvironmentAsync(shop.Resource);
        app["INNGEST_SIGNING_KEY"].ShouldBe(server["INNGEST_SIGNING_KEY"]);
        app["INNGEST_EVENT_KEY"].ShouldBe(server["INNGEST_EVENT_KEY"]);
        app["INNGEST_SIGNING_KEY"].ShouldBe(await Model.ValueAsync(inngest.Resource.SigningKeyParameter!));
    }

    [Fact]
    public async Task A_container_gets_the_container_network_address_of_a_self_hosted_server()
    {
        var builder = Model.CreateBuilder();
        var inngest = AddSelfHostedServer(builder);

        var worker = builder.AddContainer("worker", "example/worker").WithReference(inngest);

        var environment = await Model.EnvironmentAsync(worker.Resource);
        environment["INNGEST_BASE_URL"].ShouldBe("http://inngest.dev.internal:8288");
        environment["INNGEST_CONNECT_GATEWAY_URL"].ShouldBe("ws://inngest.dev.internal:8289/v0/connect");
    }

    [Fact]
    public async Task In_the_manifest_the_settings_refer_to_the_endpoint_and_the_parameters()
    {
        var builder = Model.CreateBuilder();
        var inngest = AddSelfHostedServer(builder);
        var shop = builder.AddProject<Projects.TestApp>("shop").WithReference(inngest);

        var configuration = await ExecutionConfigurationBuilder.Create(shop.Resource)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, TestContext.Current.CancellationToken);

        var environment = configuration.EnvironmentVariables.ToDictionary();
        environment["INNGEST_BASE_URL"].ShouldBe("{inngest.bindings.http.url}");
        environment["INNGEST_CONNECT_GATEWAY_URL"].ShouldBe("ws://{inngest.bindings.connect.host}:{inngest.bindings.connect.port}/v0/connect");
        environment["INNGEST_SIGNING_KEY"].ShouldBe("{signing.value}");
        environment["INNGEST_EVENT_KEY"].ShouldBe("{events.value}");
    }

    [Fact]
    public void The_dashboard_shows_the_server_as_a_resource_the_project_refers_to()
    {
        var builder = Model.CreateBuilder();
        var inngest = AddDevServer(builder);

        var shop = builder.AddProject<Projects.TestApp>("shop").WithReference(inngest);

        shop.Resource.Annotations.OfType<ResourceRelationshipAnnotation>().ShouldHaveSingleItem().Resource.ShouldBeSameAs(inngest.Resource);
    }

    // Aspire's own WithReference injects the endpoint for service discovery and a connection string; the SDK reads
    // neither, and a second set of variables to the same server would only confuse.
    [Fact]
    public async Task Only_the_settings_of_the_SDK_are_added_and_the_project_does_not_wait_for_the_server()
    {
        var builder = Model.CreateBuilder();
        var inngest = AddDevServer(builder);

        var shop = builder.AddProject<Projects.TestApp>("shop").WithReference(inngest);

        var environment = await Model.EnvironmentAsync(shop.Resource);
        environment.Keys.ShouldNotContain(key => key.StartsWith("services__", StringComparison.Ordinal));
        environment.Keys.ShouldNotContain(key => key.StartsWith("ConnectionStrings__", StringComparison.Ordinal));
        shop.Resource.Annotations.OfType<WaitAnnotation>().ShouldBeEmpty();
    }

    [Fact]
    public void The_arguments_are_checked()
    {
        var builder = Model.CreateBuilder();
        var inngest = AddDevServer(builder);
        var shop = builder.AddProject<Projects.TestApp>("shop");

        Should.Throw<ArgumentNullException>(() => shop.WithReference((IResourceBuilder<InngestServerResource>)null!));
        Should.Throw<ArgumentNullException>(() => ((IResourceBuilder<ProjectResource>)null!).WithReference(inngest));
    }
}
