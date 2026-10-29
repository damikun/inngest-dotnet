using System.Text;
using Inngest.Internal;
using Inngest.Registration;
using static Inngest.Tests.Registration.RegistryFixtures;

namespace Inngest.Tests.Registration;

public sealed class SyncBodyTests
{
    [Fact]
    public void Describes_the_app_the_sdk_and_its_capabilities()
    {
        var registry = new FunctionRegistry([], Settings(appVersion: "1.4.0"));

        Write(registry).ShouldBe(
            $$"""{"url":"https://example.com/api/inngest","deployType":"ping","appName":"my-app","appVersion":"1.4.0","sdk":"{{SdkInfo.RegistrationValue}}","v":"0.1","framework":"aspnetcore","capabilities":{"trust_probe":"v1"},"functions":[]}""");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Leaves_out_the_app_version_when_there_is_none(string? appVersion)
    {
        var registry = new FunctionRegistry([], Settings(appVersion: appVersion));

        Parse(Write(registry)).TryGetProperty("appVersion", out _).ShouldBeFalse();
    }

    [Fact]
    public void Reports_the_framework_it_is_given()
    {
        var registry = new FunctionRegistry([], Settings());

        Parse(Write(registry, framework: "minimal")).GetProperty("framework").GetString().ShouldBe("minimal");
    }

    [Fact]
    public void Leaves_out_a_blank_framework()
    {
        var registry = new FunctionRegistry([], Settings());

        Parse(Write(registry, framework: "")).TryGetProperty("framework", out _).ShouldBeFalse();
    }

    [Fact]
    public void Writes_a_minimal_function_with_only_what_inngest_requires()
    {
        Function(Orders).ShouldBe(
            """{"id":"my-app-orders","name":"orders","triggers":[{"event":"order/created"}],"steps":{"step":{"id":"step","name":"step","runtime":{"type":"http","url":"https://example.com/api/inngest?fnId=my-app-orders&stepId=step"}}}}""");
    }

    [Fact]
    public void Writes_functions_in_the_order_they_were_registered()
    {
        var registry = Registry(
            Orders with { Id = "second" },
            Orders with { Id = "first" },
            Orders with { Id = "third" });

        FunctionIds(Write(registry)).ShouldBe(["my-app-second", "my-app-first", "my-app-third"]);
    }

    [Fact]
    public void Prefixes_function_ids_with_the_app_id_without_changing_them()
    {
        var registry = new FunctionRegistry([Define(Orders with { Id = "Send Email/2" })], Settings("Shop App"));

        FunctionIds(Write(registry)).ShouldBe(["Shop App-Send Email/2"]);
    }

    [Fact]
    public void Defaults_to_the_ping_deploy_type_with_an_http_runtime()
    {
        var body = Write(Registry(Orders));

        Parse(body).GetProperty("deployType").GetString().ShouldBe("ping");
        Runtime(body).ShouldBe("""{"type":"http","url":"https://example.com/api/inngest?fnId=my-app-orders&stepId=step"}""");
    }

    [Fact]
    public void Connect_apps_register_a_websocket_runtime()
    {
        var body = Write(Registry(Orders), "wss://connect", "connect", deployType: "connect");

        Parse(body).GetProperty("deployType").GetString().ShouldBe("connect");
        Runtime(body).ShouldBe("""{"type":"ws","url":"wss://connect/?fnId=my-app-orders&stepId=step"}""");
    }

    [Fact]
    public void Appends_to_what_the_writer_already_holds()
    {
        using var writer = new PooledBufferWriter();
        writer.Write("prefix:"u8);

        Registry(Orders).WriteRegistration(writer, new Uri(DefaultServeUrl), "aspnetcore");

        Encoding.UTF8.GetString(writer.WrittenSpan).ShouldStartWith("""prefix:{"url":""");
    }

    [Fact]
    public void Expressions_are_not_escaped_beyond_what_json_requires()
    {
        var options = Orders with
        {
            Triggers = [Trigger.Event("order/created", "event.data.plan == 'pro' && event.data.total > 100")],
        };

        Property(options, "triggers").ShouldBe(
            """[{"event":"order/created","expression":"event.data.plan == 'pro' && event.data.total > 100"}]""");
    }

    [Fact]
    public void Writes_the_same_body_on_every_sync()
    {
        var registry = Registry(Orders, Orders with { Id = "refunds" });

        Write(registry).ShouldBe(Write(registry));
    }

    [Fact]
    public void Concurrent_syncs_each_write_a_complete_body()
    {
        var registry = Registry(Orders, Orders with { Id = "refunds", Retries = 3 });
        var expected = Write(registry);
        var bodies = new string[64];

        Parallel.For(0, bodies.Length, i => bodies[i] = Write(registry));

        bodies.ShouldAllBe(body => body == expected);
    }
}
