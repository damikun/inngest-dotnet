using DotNet.Testcontainers.Builders;
using static Inngest.Tests.TestingContainers.BuilderHarness;

namespace Inngest.Tests.TestingContainers;

public sealed class ConnectGatewayBuilderTests
{
    [Fact]
    public void A_gateway_port_is_picked_when_none_is_given_and_used_on_both_sides()
    {
        var container = Builder().WithConnectGateway().Build();

        var port = container.ConnectGatewayPort.ShouldNotBeNull();
        port.ShouldBeInRange(1024, ushort.MaxValue);
        new[] { 8288, 8289, 50052, 50053 }.ShouldNotContain(port);
        container.CommandOf().SkipWhile(argument => argument != "--connect-gateway-port").Skip(1).First().ShouldBe(port.ToString());
        container.Configuration.PortBindings.ValueOf(port.ToString()).ShouldBe(port.ToString());
    }

    [Fact]
    public void A_gateway_port_can_be_chosen()
    {
        var container = Builder().WithConnectGateway(18289).Build();

        container.ConnectGatewayPort.ShouldBe(18289);
        container.Configuration.PortBindings.ValueOf("18289").ShouldBe("18289");
        container.CommandOf().ShouldContain("18289");
    }

    [Fact]
    public void Without_a_gateway_nothing_but_the_api_is_published()
    {
        Builder().Build().Configuration.PortBindings.Keys.ShouldBe(["8288"]);
    }

    [Fact]
    public void The_gateway_port_joins_the_ports_the_kernel_never_hands_out()
    {
        var parameters = Builder().WithConnectGateway(18289).Build().CreateParametersOf();

        parameters.HostConfig!.Sysctls!["net.ipv4.ip_local_reserved_ports"].ShouldBe("50052-50053,18289");
    }

    [Fact]
    public void The_container_waits_for_the_gateway_as_well_as_the_api()
    {
        // The check that the container runs, the health check, and the readiness of the gateway.
        Builder().Build().Configuration.WaitStrategies.Count().ShouldBe(2);
        Builder().WithConnectGateway().Build().Configuration.WaitStrategies.Count().ShouldBe(3);
    }

    [Fact]
    public void A_wait_of_the_callers_own_is_not_replaced()
    {
        var wait = Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(8288).UntilMessageIsLogged("started");

        Builder().WithWaitStrategy(wait).Build().Configuration.WaitStrategies.Count().ShouldBe(3);
        Builder().WithWaitStrategy(wait).WithConnectGateway().Build().Configuration.WaitStrategies.Count().ShouldBe(3);
    }

    [Fact]
    public void Each_container_of_one_builder_picks_its_own_free_port()
    {
        var builder = Builder().WithConnectGateway();

        // Free ports are picked from the kernel's, so two picks in a row differ unless the first was handed out again.
        var ports = Enumerable.Range(0, 5).Select(_ => builder.Build().ConnectGatewayPort).Distinct().Count();

        ports.ShouldBeGreaterThan(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(8288)]
    [InlineData(50052)]
    [InlineData(50053)]
    public void The_gateway_port_must_be_a_port_that_the_server_does_not_use_itself(int port)
    {
        Should.Throw<ArgumentException>(() => Builder().WithConnectGateway(port).Build());
    }

    [Fact]
    public void A_self_hosted_server_can_have_a_gateway_too()
    {
        var container = Builder().WithSelfHosting().WithConnectGateway(18289).Build();

        container.CommandOf().ShouldContain("--connect-gateway-port");
        container.CommandOf().ShouldContain("start");
    }
}
