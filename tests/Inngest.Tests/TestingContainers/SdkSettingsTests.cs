using Inngest.Configuration;
using Inngest.Testing.Containers;
using InngestConfiguration = Inngest.Testing.Containers.InngestConfiguration;

namespace Inngest.Tests.TestingContainers;

// The settings are checked as the SDK resolves them, not as they were written, since the options win over each other in
// ways only the SDK knows.
public sealed class SdkSettingsTests
{
    private static readonly Uri Server = new("http://127.0.0.1:55001/");

    private const string SigningKey = "9f2c5a7e1b3d4f60a8c9e0d1b2a3f4e5d6c7b8a9f0e1d2c3b4a5968778695a4b";

    private static readonly InngestConfiguration Dev = new();

    private static readonly InngestConfiguration SelfHosted = new(selfHosted: true, signingKey: SigningKey, eventKey: "events-1");

    private static InngestSettings Resolve(Action<InngestOptions> configure, Func<string, string?>? variables = null)
    {
        var options = new InngestOptions { AppId = "shop" };
        configure(options);
        return InngestSettings.Create(options, variables ?? (_ => null));
    }

    [Fact]
    public void An_app_in_dev_mode_talks_to_the_dev_server()
    {
        var settings = Resolve(options => SdkSettings.Apply(options, Server, Dev));

        settings.IsDev.ShouldBeTrue();
        settings.ApiBaseUrl.ShouldBe(Server);
        settings.EventApiBaseUrl.ShouldBe(Server);
    }

    [Fact]
    public void An_app_in_cloud_mode_talks_to_the_self_hosted_server_with_its_keys()
    {
        var settings = Resolve(options => SdkSettings.Apply(options, Server, SelfHosted));

        settings.IsDev.ShouldBeFalse();
        settings.ApiBaseUrl.ShouldBe(Server);
        settings.EventApiBaseUrl.ShouldBe(Server);
        settings.EventKey.ShouldBe("events-1");
        settings.SigningKey.ShouldNotBeNull();
    }

    [Fact]
    public void The_addresses_the_app_set_in_code_are_replaced()
    {
        var settings = Resolve(options =>
        {
            options.ApiBaseUrl = new Uri("https://api.inngest.com");
            options.EventApiBaseUrl = new Uri("https://inn.gs");
            options.DevServerUrl = new Uri("http://localhost:8288");
            SdkSettings.Apply(options, Server, Dev);
        });

        settings.ApiBaseUrl.ShouldBe(Server);
        settings.EventApiBaseUrl.ShouldBe(Server);
    }

    [Fact]
    public void The_mode_the_app_chose_is_replaced_too()
    {
        Resolve(options =>
        {
            options.IsDev = false;
            SdkSettings.Apply(options, Server, Dev);
        }).IsDev.ShouldBeTrue();

        Resolve(options =>
        {
            options.IsDev = true;
            SdkSettings.Apply(options, Server, SelfHosted);
        }).IsDev.ShouldBeFalse();
    }

    [Fact]
    public void The_variables_of_the_developers_machine_do_not_matter_once_the_options_are_applied()
    {
        var variables = new Dictionary<string, string>
        {
            ["INNGEST_DEV"] = "http://localhost:8288",
            ["INNGEST_BASE_URL"] = "https://api.inngest.com",
            ["INNGEST_EVENT_KEY"] = "from-the-machine",
        };

        var settings = Resolve(options => SdkSettings.Apply(options, Server, SelfHosted), variables.GetValueOrDefault);

        settings.IsDev.ShouldBeFalse();
        settings.ApiBaseUrl.ShouldBe(Server);
        settings.EventKey.ShouldBe("events-1");
    }

    [Fact]
    public void The_dev_server_variables_give_an_app_the_same_settings_as_the_options()
    {
        var variables = SdkSettings.ToEnvironment(Server, Dev);

        var settings = Resolve(_ => { }, variables.GetValueOrDefault);

        variables.Keys.ShouldBe(["INNGEST_DEV"]);
        settings.IsDev.ShouldBeTrue();
        settings.ApiBaseUrl.ShouldBe(Server);
        settings.EventApiBaseUrl.ShouldBe(Server);
    }

    [Fact]
    public void The_self_hosted_variables_give_an_app_the_same_settings_as_the_options()
    {
        var variables = SdkSettings.ToEnvironment(Server, SelfHosted);

        var settings = Resolve(_ => { }, variables.GetValueOrDefault);

        variables.Keys.ShouldBe(["INNGEST_BASE_URL", "INNGEST_SIGNING_KEY", "INNGEST_EVENT_KEY"], ignoreOrder: true);
        settings.IsDev.ShouldBeFalse();
        settings.ApiBaseUrl.ShouldBe(Server);
        settings.EventApiBaseUrl.ShouldBe(Server);
        settings.EventKey.ShouldBe("events-1");
        settings.SigningKey.ShouldNotBeNull();
    }
}
