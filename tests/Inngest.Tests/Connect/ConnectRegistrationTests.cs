using Inngest.Internal;
using static Inngest.Tests.Registration.RegistryFixtures;

namespace Inngest.Tests.Connect;

// The registration writer is shared with HTTP syncs; a Connect app's body differs only where the gateway needs it.
public sealed class ConnectRegistrationTests
{
    [Fact]
    public void Connect_bodies_declare_the_connect_capability_next_to_the_trust_probe()
    {
        var body = Write(Registry(Orders), "wss://connect", "connect", deployType: "connect");

        RawProperty(Parse(body), "capabilities").ShouldBe("""{"trust_probe":"v1","connect":"v1"}""");
        Parse(body).GetProperty("deployType").GetString().ShouldBe("connect");
        Runtime(body).ShouldBe("""{"type":"ws","url":"wss://connect/?fnId=my-app-orders&stepId=step"}""");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ping")]
    public void Http_bodies_keep_only_the_trust_probe(string? deployType)
    {
        var body = Write(Registry(Orders), deployType: deployType);

        RawProperty(Parse(body), "capabilities").ShouldBe("""{"trust_probe":"v1"}""");
    }

    [Fact]
    public void Connect_bodies_are_otherwise_the_http_body()
    {
        var connect = Write(Registry(Orders), "wss://connect", "connect", deployType: "connect");

        connect.ShouldBe(
            """{"url":"wss://connect/","deployType":"connect","appName":"my-app","sdk":""" + $"\"{SdkInfo.RegistrationValue}\"" +
            ""","v":"0.1","framework":"connect","capabilities":{"trust_probe":"v1","connect":"v1"},"functions":[{"id":"my-app-orders","name":"orders","triggers":[{"event":"order/created"}],"steps":{"step":{"id":"step","name":"step","runtime":{"type":"ws","url":"wss://connect/?fnId=my-app-orders&stepId=step"}}}}]}""");
    }
}
