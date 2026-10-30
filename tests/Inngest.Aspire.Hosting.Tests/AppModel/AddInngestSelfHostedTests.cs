namespace Inngest.Aspire.Hosting.Tests.AppModel;

// The self-hosted server: inngest start with a signing key and an event key, which it reads from its environment.
public sealed class AddInngestSelfHostedTests
{
    private const string SigningKey = "0a1b2c3d4e5f60718293a4b5c6d7e8f9";

    [Fact]
    public async Task The_server_is_started_with_its_keys_from_the_environment_and_polls_for_apps()
    {
        var builder = Model.CreateBuilder();
        var inngest = builder.AddInngestSelfHosted("inngest", Model.Secret(builder, "signing", SigningKey), Model.Secret(builder, "events", "event-key-1"));
        inngest.Resource.Annotations.OfType<EndpointAnnotation>().Single(endpoint => endpoint.Name == "connect").Port = 12345;

        var arguments = await Model.ArgumentsAsync(inngest.Resource);
        var environment = await Model.EnvironmentAsync(inngest.Resource);

        // inngest start does not poll unless told to, and an app that starts after the server has to be found.
        arguments.ShouldBe(["start", "--poll-interval", "5", "--connect-gateway-port", "12345"]);
        environment["INNGEST_SIGNING_KEY"].ShouldBe(SigningKey);
        environment["INNGEST_EVENT_KEY"].ShouldBe("event-key-1");
        arguments.ShouldNotContain(argument => argument.Contains(SigningKey, StringComparison.Ordinal));
        inngest.Resource.IsSelfHosted.ShouldBeTrue();
    }

    [Fact]
    public void It_is_the_same_container_as_the_dev_server_but_for_its_command()
    {
        var builder = Model.CreateBuilder();

        var inngest = builder.AddInngestSelfHosted("inngest", port: 18288, connectPort: 8289);

        var image = inngest.Resource.Annotations.OfType<ContainerImageAnnotation>().ShouldHaveSingleItem();
        (image.Registry, image.Image, image.Tag).ShouldBe(("docker.io", "inngest/inngest", "v1.49.0"));
        inngest.Resource.Entrypoint.ShouldBe("inngest");
        inngest.Resource.Annotations.OfType<EndpointAnnotation>().Single(endpoint => endpoint.Name == "http").Port.ShouldBe(18288);
        var connect = inngest.Resource.Annotations.OfType<EndpointAnnotation>().Single(endpoint => endpoint.Name == "connect");
        (connect.Port, connect.TargetPort).ShouldBe((8289, 8289));
        inngest.Resource.Annotations.OfType<HealthCheckAnnotation>().Select(annotation => annotation.Key)
            .ShouldBe(["inngest_http_/health_200_check", "inngest_connect_/ready_200_check"]);
    }

    // inngest start refuses a signing key that is not hex of even length, and one with a signkey- prefix is not hex.
    [Fact]
    public async Task The_keys_that_are_generated_suit_the_server_and_stay_the_same()
    {
        var builder = Model.CreateBuilder();

        var inngest = builder.AddInngestSelfHosted("inngest");

        var signingKey = inngest.Resource.SigningKeyParameter!;
        signingKey.Name.ShouldBe("inngest-signing-key");
        signingKey.Secret.ShouldBeTrue();
        var signingKeyValue = await Model.ValueAsync(signingKey);
        signingKeyValue.Length.ShouldBe(64);
        signingKeyValue.ShouldAllBe(character => char.IsAsciiHexDigit(character));
        Convert.FromHexString(signingKeyValue).Length.ShouldBe(32);
        (await Model.ValueAsync(signingKey)).ShouldBe(signingKeyValue);

        // The event key is part of the URL events are posted to.
        var eventKey = inngest.Resource.EventKeyParameter!;
        eventKey.Name.ShouldBe("inngest-event-key");
        eventKey.Secret.ShouldBeTrue();
        var eventKeyValue = await Model.ValueAsync(eventKey);
        eventKeyValue.Length.ShouldBeGreaterThanOrEqualTo(22);
        eventKeyValue.ShouldAllBe(character => char.IsAsciiLetterOrDigit(character));
        eventKeyValue.ShouldNotBe(signingKeyValue);
    }

    [Fact]
    public async Task A_generated_key_can_be_overridden_in_the_configuration_of_the_AppHost()
    {
        var builder = Model.CreateBuilder();
        builder.Configuration["Parameters:inngest-signing-key"] = SigningKey;
        builder.Configuration["Parameters:inngest-event-key"] = "from-configuration";

        var inngest = builder.AddInngestSelfHosted("inngest");

        (await Model.ValueAsync(inngest.Resource.SigningKeyParameter!)).ShouldBe(SigningKey);
        (await Model.ValueAsync(inngest.Resource.EventKeyParameter!)).ShouldBe("from-configuration");
    }

    [Fact]
    public async Task Each_server_generates_keys_of_its_own()
    {
        var builder = Model.CreateBuilder();

        var first = builder.AddInngestSelfHosted("first");
        var second = builder.AddInngestSelfHosted("second");

        (await Model.ValueAsync(first.Resource.SigningKeyParameter!)).ShouldNotBe(await Model.ValueAsync(second.Resource.SigningKeyParameter!));
        (await Model.ValueAsync(first.Resource.EventKeyParameter!)).ShouldNotBe(await Model.ValueAsync(second.Resource.EventKeyParameter!));
    }

    [Fact]
    public void A_self_hosted_server_needs_both_keys_and_the_dev_server_takes_neither()
    {
        var builder = Model.CreateBuilder();
        var key = Model.Secret(builder, "key", SigningKey).Resource;

        Should.Throw<ArgumentException>(() => new InngestServerResource("inngest", key, null)).ParamName.ShouldBe("eventKey");
        Should.Throw<ArgumentException>(() => new InngestServerResource("inngest", null, key)).ParamName.ShouldBe("signingKey");
        new InngestServerResource("inngest").IsSelfHosted.ShouldBeFalse();
    }
}
