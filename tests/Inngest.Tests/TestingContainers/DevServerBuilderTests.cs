using Inngest.Testing.Containers;
using static Inngest.Tests.TestingContainers.BuilderHarness;

namespace Inngest.Tests.TestingContainers;

public sealed class DevServerBuilderTests
{
    [Fact]
    public void The_default_container_runs_the_dev_server_without_discovery_or_polling_and_with_fast_timing()
    {
        Builder().CommandOf().ShouldBe(
            ["inngest", "dev", "--no-discovery", "--no-poll", "--poll-interval", "1", "--retry-interval", "1", "--tick", "50"]);
    }

    [Fact]
    public void The_image_is_the_one_given()
    {
        var container = Builder().Build();

        container.Image.FullName.ShouldBe(Image);
        container.IsDevServer.ShouldBeTrue();
    }

    [Fact]
    public void The_dev_server_has_neither_keys_nor_a_gateway()
    {
        var container = Builder().Build();

        container.SigningKey.ShouldBeNull();
        container.EventKey.ShouldBeNull();
        container.ConnectGatewayPort.ShouldBeNull();
    }

    [Fact]
    public void Fast_timing_can_be_turned_off_and_on_again()
    {
        Builder().WithFastTiming(false).CommandOf().ShouldBe(["inngest", "dev", "--no-discovery", "--no-poll"]);
        Builder().WithFastTiming(false).WithFastTiming().CommandOf().ShouldContain("--tick");
    }

    [Fact]
    public void The_api_is_published_on_a_random_port_of_the_host()
    {
        // Docker takes an empty host port as "any free port".
        Builder().Build().Configuration.PortBindings.ShouldContainKeyAndValue("8288", "");
    }

    [Fact]
    public void The_container_reaches_the_host_through_the_host_gateway_so_that_linux_resolves_the_name_too()
    {
        Builder().Build().Configuration.ExtraHosts.ShouldContain("host.docker.internal:host-gateway");
    }

    [Fact]
    public void The_server_does_not_report_usage()
    {
        Builder().Build().Configuration.Environments.ShouldContainKeyAndValue("DO_NOT_TRACK", "1");
    }

    [Fact]
    public void The_kernel_is_kept_from_handing_the_connect_ports_to_connections_as_source_ports()
    {
        var parameters = Builder().Build().CreateParametersOf();

        parameters.HostConfig!.Sysctls!.ShouldContainKeyAndValue("net.ipv4.ip_local_reserved_ports", "50052-50053");
    }

    [Fact]
    public void An_app_url_is_synced_at_the_hosts_address_as_the_container_sees_it()
    {
        Builder().WithAppUrl("http://localhost:5000/api/inngest").CommandOf().TakeLast(2).ShouldBe(["-u", "http://host.docker.internal:5000/api/inngest"]);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5000/api/inngest", "http://host.docker.internal:5000/api/inngest")]
    [InlineData("http://[::1]:5000/api/inngest", "http://host.docker.internal:5000/api/inngest")]
    [InlineData("http://0.0.0.0:5000/api/inngest?env=test", "http://host.docker.internal:5000/api/inngest?env=test")]
    [InlineData("https://localhost:5001/inngest", "https://host.docker.internal:5001/inngest")]
    [InlineData("http://shop:8080/api/inngest", "http://shop:8080/api/inngest")]
    [InlineData("https://shop.example.com/api/inngest", "https://shop.example.com/api/inngest")]
    public void Only_addresses_that_mean_the_host_are_rewritten(string appUrl, string seenByContainer)
    {
        Builder().WithAppUrl(new Uri(appUrl)).CommandOf().TakeLast(2).ShouldBe(["-u", seenByContainer]);
    }

    [Fact]
    public void Every_app_url_gets_a_flag_in_the_order_given()
    {
        var command = Builder().WithAppUrl("http://localhost:5000/a").WithAppUrl("http://localhost:5001/b").CommandOf();

        command.TakeLast(4).ShouldBe(["-u", "http://host.docker.internal:5000/a", "-u", "http://host.docker.internal:5001/b"]);
    }

    [Theory]
    [InlineData("/api/inngest")]
    [InlineData("localhost:5000")]
    [InlineData("ftp://localhost/inngest")]
    public void An_app_url_must_be_an_absolute_http_url(string appUrl)
    {
        Should.Throw<Exception>(() => Builder().WithAppUrl(appUrl));
    }

    [Fact]
    public void Arguments_added_with_a_command_come_after_the_ones_the_builder_makes()
    {
        var command = Builder().WithCommand("--verbose").WithAppUrl("http://localhost:5000/api/inngest").CommandOf();

        command.Take(2).ShouldBe(["inngest", "dev"]);
        command.TakeLast(3).ShouldBe(["-u", "http://host.docker.internal:5000/api/inngest", "--verbose"]);
    }

    [Fact]
    public void The_order_of_the_calls_does_not_matter()
    {
        var first = Builder().WithAppUrl("http://localhost:5000/api/inngest").WithFastTiming(false).CommandOf();
        var second = Builder().WithFastTiming(false).WithAppUrl("http://localhost:5000/api/inngest").CommandOf();

        first.ShouldBe(second);
    }

    [Fact]
    public void Building_does_not_change_the_builder()
    {
        var builder = Builder().WithCommand("--verbose");

        builder.CommandOf().ShouldBe(builder.CommandOf());
        builder.CommandOf().Count(argument => argument == "inngest").ShouldBe(1);
    }

    [Fact]
    public void A_signing_key_or_an_event_key_needs_the_self_hosted_server()
    {
        Should.Throw<ArgumentException>(() => Builder().WithSigningKey("aabb").Build()).Message.ShouldContain("WithSelfHosting");
        Should.Throw<ArgumentException>(() => Builder().WithEventKey("key").Build()).Message.ShouldContain("WithSelfHosting");
    }

    [Fact]
    public void An_image_can_be_given_as_text_or_as_an_image()
    {
        new InngestBuilder(new DotNet.Testcontainers.Images.DockerImage(Image)).WithDockerEndpoint("tcp://127.0.0.1:1")
            .Build().Image.FullName.ShouldBe(Image);
    }
}
