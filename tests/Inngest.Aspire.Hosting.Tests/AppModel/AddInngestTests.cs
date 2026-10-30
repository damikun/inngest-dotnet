using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Inngest.Aspire.Hosting.Tests.AppModel;

// The container that AddInngest describes: the image, the command, the endpoints and what the dashboard shows of them.
public sealed class AddInngestTests
{
    [Fact]
    public void The_resource_runs_the_pinned_image_of_the_dev_server()
    {
        var builder = Model.CreateBuilder();

        var inngest = builder.AddInngest("inngest");

        // The tag is the version of the Inngest CLI that this repository tests against, so a change shows up here.
        var image = inngest.Resource.Annotations.OfType<ContainerImageAnnotation>().ShouldHaveSingleItem();
        image.Registry.ShouldBe("docker.io");
        image.Image.ShouldBe("inngest/inngest");
        image.Tag.ShouldBe("v1.49.0");
        inngest.Resource.Entrypoint.ShouldBe("inngest");
        inngest.Resource.IsSelfHosted.ShouldBeFalse();
        inngest.Resource.SigningKeyParameter.ShouldBeNull();
        inngest.Resource.EventKeyParameter.ShouldBeNull();
    }

    [Fact]
    public void The_tag_can_be_replaced_like_that_of_any_container()
    {
        var builder = Model.CreateBuilder();

        var inngest = builder.AddInngest("inngest").WithImageTag("v1.50.0");

        inngest.Resource.Annotations.OfType<ContainerImageAnnotation>().ShouldHaveSingleItem().Tag.ShouldBe("v1.50.0");
    }

    [Fact]
    public void The_API_and_UI_are_one_endpoint_that_Aspire_proxies_on_a_port_of_its_choice()
    {
        var builder = Model.CreateBuilder();

        var inngest = builder.AddInngest("inngest");

        var http = inngest.Resource.Annotations.OfType<EndpointAnnotation>().Single(endpoint => endpoint.Name == "http");
        http.TargetPort.ShouldBe(8288);
        http.Port.ShouldBeNull();
        http.UriScheme.ShouldBe("http");
        http.Protocol.ShouldBe(ProtocolType.Tcp);
        http.IsExplicitlyProxied.ShouldBeNull();
        inngest.Resource.PrimaryEndpoint.EndpointName.ShouldBe("http");
    }

    [Fact]
    public void A_port_can_be_chosen_for_the_API()
    {
        var builder = Model.CreateBuilder();

        var inngest = builder.AddInngest("inngest", port: 18288);

        inngest.Resource.Annotations.OfType<EndpointAnnotation>().Single(endpoint => endpoint.Name == "http").Port.ShouldBe(18288);
    }

    // Workers dial the gateway at the host they called the API on and the port the server listens on, so the port must
    // be the same outside and inside the container, and a proxy in between would break that.
    [Fact]
    public void The_gateway_endpoint_has_the_same_port_on_the_host_and_in_the_container()
    {
        var builder = Model.CreateBuilder();

        var inngest = builder.AddInngest("inngest");

        var connect = inngest.Resource.Annotations.OfType<EndpointAnnotation>().Single(endpoint => endpoint.Name == "connect");
        connect.IsExplicitlyProxied.ShouldBe(false);
        connect.Port.ShouldBeNull();
        connect.TargetPort.ShouldBeNull();
        inngest.Resource.ConnectEndpoint.EndpointName.ShouldBe("connect");

        // Once Aspire has picked a port for the host, the container listens on it too.
        connect.Port = 12345;
        connect.TargetPort.ShouldBe(12345);
    }

    [Fact]
    public void A_port_can_be_chosen_for_the_gateway()
    {
        var builder = Model.CreateBuilder();

        var inngest = builder.AddInngest("inngest", connectPort: 8289);

        var connect = inngest.Resource.Annotations.OfType<EndpointAnnotation>().Single(endpoint => endpoint.Name == "connect");
        connect.Port.ShouldBe(8289);
        connect.TargetPort.ShouldBe(8289);
        connect.IsExplicitlyProxied.ShouldBe(false);
    }

    [Fact]
    public async Task The_dev_server_is_started_without_discovery_and_told_which_port_its_gateway_has()
    {
        var builder = Model.CreateBuilder();
        var inngest = builder.AddInngest("inngest");
        Model.Allocate(inngest.Resource, "http", 18288, "inngest.dev.internal", 8288);
        inngest.Resource.Annotations.OfType<EndpointAnnotation>().Single(endpoint => endpoint.Name == "connect").Port = 12345;

        var arguments = await Model.ArgumentsAsync(inngest.Resource);

        // Inside the container, discovery would scan the container's own ports and find nothing.
        arguments.ShouldBe(["dev", "--no-discovery", "--connect-gateway-port", "12345"]);
        (await Model.EnvironmentAsync(inngest.Resource)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_gateway_port_chosen_in_code_is_the_one_the_server_listens_on()
    {
        var builder = Model.CreateBuilder();
        var inngest = builder.AddInngest("inngest", connectPort: 28289);

        var arguments = await Model.ArgumentsAsync(inngest.Resource);

        arguments.ShouldBe(["dev", "--no-discovery", "--connect-gateway-port", "28289"]);
    }

    [Fact]
    public void The_server_is_healthy_when_its_API_and_its_gateway_answer()
    {
        var builder = Model.CreateBuilder();

        var inngest = builder.AddInngest("inngest");

        inngest.Resource.Annotations.OfType<HealthCheckAnnotation>().Select(annotation => annotation.Key)
            .ShouldBe(["inngest_http_/health_200_check", "inngest_connect_/ready_200_check"]);
        using var app = builder.Build();
        var registrations = app.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        registrations.Select(registration => registration.Name).ShouldBe(["inngest_http_/health_200_check", "inngest_connect_/ready_200_check"], ignoreOrder: true);
    }

    [Fact]
    public async Task The_dashboard_links_the_UI_and_keeps_the_gateway_to_the_details()
    {
        var builder = Model.CreateBuilder();
        var inngest = builder.AddInngest("inngest");
        List<ResourceUrlAnnotation> urls =
        [
            new() { Url = "http://localhost:18288", Endpoint = inngest.Resource.PrimaryEndpoint },
            new() { Url = "http://localhost:12345", Endpoint = inngest.Resource.ConnectEndpoint },
        ];
        var context = new ResourceUrlsCallbackContext(new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run), inngest.Resource, urls);

        foreach (var callback in inngest.Resource.Annotations.OfType<ResourceUrlsCallbackAnnotation>())
        {
            await callback.Callback(context);
        }

        var ui = urls.Single(url => url.Endpoint?.EndpointName == "http");
        ui.DisplayText.ShouldBe("Inngest UI");
        ui.DisplayLocation.ShouldBe(UrlDisplayLocation.SummaryAndDetails);
        urls.Single(url => url.Endpoint?.EndpointName == "connect").DisplayLocation.ShouldBe(UrlDisplayLocation.DetailsOnly);
    }

    [Fact]
    public void The_name_must_be_given()
    {
        var builder = Model.CreateBuilder();
        var empty = new string(' ', 0);

        Should.Throw<ArgumentNullException>(() => builder.AddInngest(null!));
        Should.Throw<ArgumentException>(() => builder.AddInngest(empty));
        Should.Throw<ArgumentNullException>(() => ((IDistributedApplicationBuilder)null!).AddInngest("inngest"));
    }
}
