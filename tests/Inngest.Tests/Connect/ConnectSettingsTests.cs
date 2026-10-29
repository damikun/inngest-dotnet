using Inngest.Connect;

namespace Inngest.Tests.Connect;

public sealed class ConnectSettingsTests
{
    private static readonly Dictionary<string, string?> NoVariables = [];

    [Fact]
    public void Defaults_to_the_machine_name_and_no_limits()
    {
        var settings = ConnectSettings.Create(new ConnectOptions(), Read(NoVariables));

        settings.InstanceId.ShouldBe(Environment.MachineName);
        settings.MaxWorkerConcurrency.ShouldBeNull();
        settings.GatewayUrl.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void A_blank_instance_id_counts_as_unset(string instanceId)
    {
        ConnectSettings.Create(new ConnectOptions { InstanceId = instanceId }, Read(NoVariables)).InstanceId.ShouldBe(Environment.MachineName);
    }

    [Fact]
    public void Reads_the_variables_for_what_the_options_leave_unset()
    {
        var settings = ConnectSettings.Create(
            new ConnectOptions(),
            Read(new()
            {
                ["INNGEST_CONNECT_MAX_WORKER_CONCURRENCY"] = "25",
                ["INNGEST_CONNECT_GATEWAY_URL"] = "ws://localhost:8100/v0/connect",
            }));

        settings.MaxWorkerConcurrency.ShouldBe(25);
        settings.GatewayUrl.ShouldBe(new Uri("ws://localhost:8100/v0/connect"));
    }

    [Fact]
    public void Options_win_over_the_variables()
    {
        var settings = ConnectSettings.Create(
            new ConnectOptions { InstanceId = " worker-1 ", MaxWorkerConcurrency = 4, GatewayUrl = new Uri("wss://gateway.example.com/v0/connect") },
            Read(new()
            {
                ["INNGEST_CONNECT_MAX_WORKER_CONCURRENCY"] = "25",
                ["INNGEST_CONNECT_GATEWAY_URL"] = "ws://localhost:8100/v0/connect",
            }));

        settings.InstanceId.ShouldBe("worker-1");
        settings.MaxWorkerConcurrency.ShouldBe(4);
        settings.GatewayUrl.ShouldBe(new Uri("wss://gateway.example.com/v0/connect"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Rejects_a_concurrency_limit_that_is_not_positive(int limit)
    {
        Should.Throw<InvalidOperationException>(() => ConnectSettings.Create(new ConnectOptions { MaxWorkerConcurrency = limit }, Read(NoVariables)))
            .Message.ShouldContain("MaxWorkerConcurrency");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("many")]
    [InlineData("1.5")]
    public void Rejects_a_concurrency_variable_that_is_not_a_positive_number(string value)
    {
        Should.Throw<InvalidOperationException>(() =>
                ConnectSettings.Create(new ConnectOptions(), Read(new() { ["INNGEST_CONNECT_MAX_WORKER_CONCURRENCY"] = value })))
            .Message.ShouldContain("INNGEST_CONNECT_MAX_WORKER_CONCURRENCY");
    }

    [Theory]
    [InlineData("http://localhost:8289/v0/connect")]
    [InlineData("localhost:8289")]
    [InlineData("/v0/connect")]
    public void Rejects_a_gateway_variable_that_is_not_a_websocket_url(string value)
    {
        Should.Throw<InvalidOperationException>(() =>
                ConnectSettings.Create(new ConnectOptions(), Read(new() { ["INNGEST_CONNECT_GATEWAY_URL"] = value })))
            .Message.ShouldContain("INNGEST_CONNECT_GATEWAY_URL");
    }

    [Fact]
    public void Rejects_a_gateway_option_that_is_not_a_websocket_url()
    {
        Should.Throw<InvalidOperationException>(() =>
                ConnectSettings.Create(new ConnectOptions { GatewayUrl = new Uri("https://gateway.example.com") }, Read(NoVariables)))
            .Message.ShouldContain("ConnectOptions.GatewayUrl");
    }

    private static Func<string, string?> Read(Dictionary<string, string?> variables) =>
        name => variables.TryGetValue(name, out var value) ? value : null;
}
