using System.Text.Json;
using System.Text.RegularExpressions;

namespace Inngest.Aspire.Hosting.Tests.AppModel;

// What `aspire publish` writes for each server. The dev server checks no signatures, so a deployment of a manifest that
// had it, or that put INNGEST_DEV into an app, would run the app without them.
public sealed partial class ManifestTests
{
    private static JsonElement Resources(JsonDocument manifest) => manifest.RootElement.GetProperty("resources");

    private static IEnumerable<string> Names(JsonElement element) => element.EnumerateObject().Select(property => property.Name);

    // Every {resource.…} in the manifest is a reference that the deployment resolves, and it needs the resource.
    private static void NothingDangles(JsonDocument manifest)
    {
        var names = Names(Resources(manifest)).ToHashSet();
        foreach (var reference in Reference().Matches(manifest.RootElement.GetRawText()).Select(match => match.Groups[1].Value))
        {
            names.ShouldContain(reference);
        }
    }

    [Fact]
    public async Task The_dev_server_is_left_out_of_the_manifest_and_a_reference_to_it_adds_no_settings()
    {
        using var manifest = await Model.ManifestAsync(builder =>
        {
            var inngest = builder.AddInngest("inngest");
            var shop = builder.AddProject<Projects.TestApp>("shop").WithHttpEndpoint(name: "http").WithReference(inngest).WaitFor(inngest);
            inngest.WithApp(shop);
        });

        var resources = Resources(manifest);
        Names(resources).ShouldBe(["shop"]);
        Names(resources.GetProperty("shop").GetProperty("env")).ShouldNotContain(name => name.StartsWith("INNGEST_", StringComparison.Ordinal));
        manifest.RootElement.GetRawText().ShouldNotContain("{inngest.", Case.Sensitive);
        NothingDangles(manifest);
    }

    [Fact]
    public async Task The_self_hosted_server_is_in_the_manifest_with_its_keys_as_parameters()
    {
        using var manifest = await Model.ManifestAsync(builder =>
        {
            var inngest = builder.AddInngestSelfHosted("inngest");
            var shop = builder.AddProject<Projects.TestApp>("shop").WithHttpEndpoint(name: "http").WithReference(inngest);
            inngest.WithApp(shop);
        });

        var resources = Resources(manifest);
        Names(resources).ShouldBe(["inngest", "shop", "inngest-signing-key", "inngest-event-key"], ignoreOrder: true);

        var server = resources.GetProperty("inngest");
        server.GetProperty("type").GetString().ShouldBe("container.v0");
        server.GetProperty("image").GetString().ShouldBe($"{InngestContainerImageTags.Registry}/{InngestContainerImageTags.Image}:{InngestContainerImageTags.Tag}");
        server.GetProperty("entrypoint").GetString().ShouldBe("inngest");
        server.GetProperty("args").EnumerateArray().Select(argument => argument.GetString())
            .ShouldBe(["start", "--poll-interval", "5", "-u", "{shop.bindings.http.url}/api/inngest"]);
        server.GetProperty("env").GetProperty("INNGEST_SIGNING_KEY").GetString().ShouldBe("{inngest-signing-key.value}");
        server.GetProperty("env").GetProperty("INNGEST_EVENT_KEY").GetString().ShouldBe("{inngest-event-key.value}");
        server.GetProperty("bindings").GetProperty("http").GetProperty("targetPort").GetInt32().ShouldBe(8288);
        server.GetProperty("bindings").GetProperty("connect").GetProperty("targetPort").GetInt32().ShouldBe(8289);

        // The parameters are secrets that the deployment generates, a signing key of digits as inngest start wants hex.
        foreach (var name in new[] { "inngest-signing-key", "inngest-event-key" })
        {
            var input = resources.GetProperty(name).GetProperty("inputs").GetProperty("value");
            input.GetProperty("secret").GetBoolean().ShouldBeTrue();
            input.GetProperty("default").TryGetProperty("generate", out _).ShouldBeTrue();
        }

        var generated = resources.GetProperty("inngest-signing-key").GetProperty("inputs").GetProperty("value").GetProperty("default").GetProperty("generate");
        generated.GetProperty("minLength").GetInt32().ShouldBe(64);
        generated.GetProperty("lower").GetBoolean().ShouldBeFalse();
        generated.GetProperty("upper").GetBoolean().ShouldBeFalse();
        generated.GetProperty("special").GetBoolean().ShouldBeFalse();

        var shopEnvironment = resources.GetProperty("shop").GetProperty("env");
        shopEnvironment.GetProperty("INNGEST_DEV").GetString().ShouldBe("0");
        shopEnvironment.GetProperty("INNGEST_BASE_URL").GetString().ShouldBe("{inngest.bindings.http.url}");
        shopEnvironment.GetProperty("INNGEST_API_BASE_URL").GetString().ShouldBe("{inngest.bindings.http.url}");
        shopEnvironment.GetProperty("INNGEST_EVENT_API_BASE_URL").GetString().ShouldBe("{inngest.bindings.http.url}");
        shopEnvironment.GetProperty("INNGEST_SIGNING_KEY").GetString().ShouldBe("{inngest-signing-key.value}");
        shopEnvironment.GetProperty("INNGEST_EVENT_KEY").GetString().ShouldBe("{inngest-event-key.value}");
        shopEnvironment.GetProperty("INNGEST_CONNECT_GATEWAY_URL").GetString().ShouldBe("ws://{inngest.bindings.connect.host}:{inngest.bindings.connect.port}/v0/connect");
        NothingDangles(manifest);
    }

    [Fact]
    public async Task With_both_servers_the_manifest_has_the_self_hosted_one_and_what_refers_to_it()
    {
        using var manifest = await Model.ManifestAsync(builder =>
        {
            var dev = builder.AddInngest("dev");
            var signed = builder.AddInngestSelfHosted("signed");
            builder.AddProject<Projects.TestApp>("shop").WithHttpEndpoint(name: "http").WithReference(dev).WithReference(signed);
            builder.AddProject<Projects.TestApp>("worker").WithHttpEndpoint(name: "http").WithReference(dev);
        });

        var resources = Resources(manifest);
        Names(resources).ShouldNotContain("dev");
        Names(resources).ShouldContain("signed");
        resources.GetProperty("shop").GetProperty("env").GetProperty("INNGEST_BASE_URL").GetString().ShouldBe("{signed.bindings.http.url}");
        Names(resources.GetProperty("worker").GetProperty("env")).ShouldNotContain(name => name.StartsWith("INNGEST_", StringComparison.Ordinal));
        NothingDangles(manifest);
    }

    [GeneratedRegex(@"\{([A-Za-z0-9_-]+)\.")]
    private static partial Regex Reference();
}
