namespace Inngest.Aspire.Hosting.Tests.AppModel;

// How the address of an app gets into the arguments of the server, where it is resolved for the server's container.
public sealed class WithAppTests
{
    // Arguments that refer to the gateway wait for Aspire to pick its port, so these servers have one. The project
    // listens on a port of the host, and the container reaches it through a tunnel on an address and port of its own.
    private static IResourceBuilder<ProjectResource> AddShop(IDistributedApplicationBuilder builder)
    {
        var shop = builder.AddProject<Projects.TestApp>("shop").WithHttpEndpoint(name: "http");
        Model.Allocate(shop.Resource, "http", 5280, "aspire.dev.internal", 40123);
        return shop;
    }

    [Fact]
    public async Task The_server_syncs_the_app_at_the_address_the_container_reaches_it_on()
    {
        var builder = Model.CreateBuilder();
        var inngest = builder.AddInngest("inngest", connectPort: 8289);
        var shop = AddShop(builder);

        inngest.WithApp(shop);

        var arguments = await Model.ArgumentsAsync(inngest.Resource);
        arguments[arguments.ToList().IndexOf("-u") + 1].ShouldBe("http://aspire.dev.internal:40123/api/inngest");
        arguments.ShouldNotContain(argument => argument.Contains("localhost", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_arguments_of_the_dev_server_come_first_and_an_app_adds_two()
    {
        var builder = Model.CreateBuilder();
        var inngest = builder.AddInngest("inngest", connectPort: 8289);
        var shop = AddShop(builder);

        inngest.WithApp(shop);

        (await Model.ArgumentsAsync(inngest.Resource))
            .ShouldBe(["dev", "--no-discovery", "--connect-gateway-port", "8289", "-u", "http://aspire.dev.internal:40123/api/inngest"]);
    }

    [Fact]
    public async Task The_self_hosted_server_takes_apps_the_same_way()
    {
        var builder = Model.CreateBuilder();
        var inngest = builder.AddInngestSelfHosted("inngest", connectPort: 8289);
        var shop = AddShop(builder);

        inngest.WithApp(shop);

        (await Model.ArgumentsAsync(inngest.Resource))
            .ShouldBe(["start", "--poll-interval", "5", "--connect-gateway-port", "8289", "-u", "http://aspire.dev.internal:40123/api/inngest"]);
    }

    [Theory]
    [InlineData("/hooks/inngest", "/hooks/inngest")]
    [InlineData("hooks/inngest", "/hooks/inngest")]
    [InlineData("/", "/")]
    public async Task The_path_of_the_endpoint_can_be_chosen(string path, string expected)
    {
        var builder = Model.CreateBuilder();
        var inngest = builder.AddInngest("inngest", connectPort: 8289);
        var shop = AddShop(builder);

        inngest.WithApp(shop, path);

        (await Model.ArgumentsAsync(inngest.Resource)).ShouldContain($"http://aspire.dev.internal:40123{expected}");
    }

    [Fact]
    public async Task Every_app_gets_an_argument_of_its_own_in_the_order_they_were_added()
    {
        var builder = Model.CreateBuilder();
        var inngest = builder.AddInngest("inngest", connectPort: 8289);
        var shop = AddShop(builder);
        var worker = builder.AddProject<Projects.TestApp>("worker").WithHttpEndpoint(name: "internal");
        Model.Allocate(worker.Resource, "internal", 5281, "aspire.dev.internal", 40124);

        inngest.WithApp(shop).WithApp(worker, "/inngest", endpointName: "internal");

        (await Model.ArgumentsAsync(inngest.Resource)).Skip(4)
            .ShouldBe(["-u", "http://aspire.dev.internal:40123/api/inngest", "-u", "http://aspire.dev.internal:40124/inngest"]);
    }

    [Fact]
    public async Task An_app_that_is_a_container_is_reached_by_its_name_on_the_container_network()
    {
        var builder = Model.CreateBuilder();
        var inngest = builder.AddInngest("inngest", connectPort: 8289);
        var shop = builder.AddContainer("shop", "example/shop").WithHttpEndpoint(targetPort: 8080, name: "http");
        Model.Allocate(shop.Resource, "http", 5280, "shop.dev.internal", 8080);

        inngest.WithApp(shop);

        (await Model.ArgumentsAsync(inngest.Resource)).ShouldContain("http://shop.dev.internal:8080/api/inngest");
    }

    [Fact]
    public void The_dashboard_shows_the_app_as_a_resource_the_server_refers_to()
    {
        var builder = Model.CreateBuilder();
        var inngest = builder.AddInngest("inngest", connectPort: 8289);
        var shop = AddShop(builder);

        inngest.WithApp(shop);

        inngest.Resource.Annotations.OfType<ResourceRelationshipAnnotation>().ShouldHaveSingleItem().Resource.ShouldBeSameAs(shop.Resource);
    }

    [Fact]
    public async Task An_endpoint_that_does_not_exist_is_reported_when_the_arguments_are_resolved_with_the_ones_the_app_has()
    {
        var builder = Model.CreateBuilder();
        var inngest = builder.AddInngest("inngest", connectPort: 8289);
        var shop = AddShop(builder);

        // The app may still get its endpoints after it is registered, so nothing is checked before the server starts.
        inngest.WithApp(shop, endpointName: "https");

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => Model.ArgumentsAsync(inngest.Resource));
        failure.Message.ShouldContain("`https`");
        failure.Message.ShouldContain("`http`");
    }

    [Fact]
    public void The_arguments_are_checked()
    {
        var builder = Model.CreateBuilder();
        var inngest = builder.AddInngest("inngest", connectPort: 8289);
        var shop = AddShop(builder);

        Should.Throw<ArgumentNullException>(() => inngest.WithApp<ProjectResource>(null!));
        Should.Throw<ArgumentException>(() => inngest.WithApp(shop, path: " "));
        Should.Throw<ArgumentException>(() => inngest.WithApp(shop, endpointName: ""));
        Should.Throw<ArgumentNullException>(() => ((IResourceBuilder<InngestServerResource>)null!).WithApp(shop));
    }
}
