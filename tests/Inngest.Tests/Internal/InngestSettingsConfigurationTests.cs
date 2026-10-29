using Inngest.Internal;

namespace Inngest.Tests.Internal;

// How InngestSettings lays the configuration section below the options and the INNGEST_* variables.
public sealed class InngestSettingsConfigurationTests
{
    private const string SigningKeyValue = "signkey-prod-12345678";
    private const string FallbackKeyValue = "signkey-prod-abcdef01";

    // The app id is set in code unless a test is about where it comes from.
    private static InngestSettings Create(
        (string Key, string? Value)[] section,
        Action<InngestOptions>? configure = null,
        (string Name, string? Value)[]? variables = null)
    {
        var options = new InngestOptions { AppId = "shop", Configuration = InngestSection.Of(section) };
        configure?.Invoke(options);
        return InngestSettings.Create(options, name => variables?.FirstOrDefault(variable => variable.Name == name).Value);
    }

    [Fact]
    public void The_app_id_comes_from_the_options_and_then_the_section()
    {
        Create([("AppId", "billing")], options => options.AppId = "payments").AppId.ShouldBe("payments");
        Create([("AppId", "billing")], options => options.AppId = null).AppId.ShouldBe("billing");
        Create([("AppId", "billing")], options => options.AppId = "  ").AppId.ShouldBe("billing");
        Create([("AppId", "  billing  ")], options => options.AppId = null).AppId.ShouldBe("billing");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_app_id_missing_everywhere_is_rejected(string? inSection)
    {
        var exception = Should.Throw<InvalidOperationException>(() =>
            Create([("AppId", inSection), ("EventKey", "event-key")], options => options.AppId = null));

        exception.Message.ShouldContain("InngestOptions.AppId is required");
        exception.Message.ShouldContain("configuration section");
    }

    [Fact]
    public void Text_keys_set_their_settings()
    {
        var settings = Create(
        [
            ("AppVersion", "2026.10.3"),
            ("EventKey", "event-key"),
            ("SigningKey", SigningKeyValue),
            ("SigningKeyFallback", FallbackKeyValue),
            ("Environment", "staging"),
            ("ServePath", "api/inngest"),
        ]);

        settings.AppVersion.ShouldBe("2026.10.3");
        settings.EventKey.ShouldBe("event-key");
        settings.SigningKey!.Value.ShouldBe(SigningKeyValue);
        settings.SigningKeyFallback!.Value.ShouldBe(FallbackKeyValue);
        settings.Environment.ShouldBe("staging");
        settings.ServePath.ShouldBe("/api/inngest");
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    public void IsDev_selects_the_mode(string value, bool expected)
    {
        var settings = Create([("IsDev", value)]);

        settings.IsDev.ShouldBe(expected);
        settings.ApiBaseUrl.AbsoluteUri.ShouldBe(expected ? "http://localhost:8288/" : "https://api.inngest.com/");
        settings.EventApiBaseUrl.AbsoluteUri.ShouldBe(expected ? "http://localhost:8288/" : "https://inn.gs/");
    }

    [Fact]
    public void The_address_keys_set_the_addresses()
    {
        var settings = Create(
        [
            ("ApiBaseUrl", "https://api.example.com"),
            ("EventApiBaseUrl", "https://events.example.com"),
            ("ServeOrigin", "https://shop.example.com"),
        ]);

        settings.ApiBaseUrl.Host.ShouldBe("api.example.com");
        settings.EventApiBaseUrl.Host.ShouldBe("events.example.com");
        settings.ServeOrigin.ShouldBe(new Uri("https://shop.example.com"));
    }

    [Fact]
    public void BaseUrl_sets_both_addresses()
    {
        var settings = Create([("BaseUrl", "https://inngest.example.com/gateway/")]);

        settings.ApiBaseUrl.AbsoluteUri.ShouldBe("https://inngest.example.com/gateway/");
        settings.EventApiBaseUrl.AbsoluteUri.ShouldBe("https://inngest.example.com/gateway/");
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    public void The_sync_keys_set_the_sync_settings(string value, bool expected)
    {
        var settings = Create([("AllowInBandSync", value), ("AllowUnsignedSync", value)]);

        settings.AllowInBandSync.ShouldBe(expected);
        settings.AllowUnsignedSync.ShouldBe(expected);
    }

    [Fact]
    public void A_section_without_these_keys_leaves_the_defaults()
    {
        var settings = Create([("Unknown", "value")]);

        settings.Mode.ShouldBe(InngestMode.Cloud);
        settings.ApiBaseUrl.AbsoluteUri.ShouldBe("https://api.inngest.com/");
        settings.EventApiBaseUrl.AbsoluteUri.ShouldBe("https://inn.gs/");
        settings.EventKey.ShouldBeNull();
        settings.SigningKey.ShouldBeNull();
        settings.Environment.ShouldBeNull();
        settings.ServeOrigin.ShouldBeNull();
        settings.ServePath.ShouldBeNull();
        settings.AllowInBandSync.ShouldBeTrue();
        settings.AllowUnsignedSync.ShouldBeTrue();
        settings.Checkpointing.ShouldBeNull();
    }

    [Fact]
    public void The_Checkpointing_subsection_sets_checkpointing()
    {
        var settings = Create([("Checkpointing:MaxRuntime", "30s"), ("Checkpointing:BufferedSteps", "3"), ("Checkpointing:MaxInterval", "00:00:05")]);

        settings.Checkpointing.ShouldBe(new CheckpointingOptions
        {
            MaxRuntime = TimeSpan.FromSeconds(30),
            BufferedSteps = 3,
            MaxInterval = TimeSpan.FromSeconds(5),
        });
    }

    [Fact]
    public void Checkpointing_in_the_options_wins_over_the_section()
    {
        var fromOptions = new CheckpointingOptions { Enabled = false };

        var settings = Create([("Checkpointing:BufferedSteps", "3")], options => options.Checkpointing = fromOptions);

        settings.Checkpointing.ShouldBeSameAs(fromOptions);
    }

    [Fact]
    public void A_checkpointing_setting_Inngest_cannot_work_with_fails_naming_its_key_in_the_section() =>
        Should.Throw<InvalidOperationException>(() => Create([("Checkpointing:BufferedSteps", "0")]))
            .Message.ShouldBe("Inngest:Checkpointing:BufferedSteps must be at least 1, but was 0.");

    [Fact]
    public void Keys_in_variables_win_over_the_section_and_keys_in_the_options_win_over_both()
    {
        (string, string?)[] section =
        [
            ("EventKey", "event-section"),
            ("SigningKey", "signkey-prod-00000001"),
            ("SigningKeyFallback", "signkey-prod-00000002"),
        ];
        (string, string?)[] variables =
        [
            ("INNGEST_EVENT_KEY", "event-env"),
            ("INNGEST_SIGNING_KEY", "signkey-prod-00000003"),
            ("INNGEST_SIGNING_KEY_FALLBACK", "signkey-prod-00000004"),
        ];

        var fromSection = Create(section);
        fromSection.EventKey.ShouldBe("event-section");
        fromSection.SigningKey!.Value.ShouldBe("signkey-prod-00000001");
        fromSection.SigningKeyFallback!.Value.ShouldBe("signkey-prod-00000002");

        var fromVariables = Create(section, variables: variables);
        fromVariables.EventKey.ShouldBe("event-env");
        fromVariables.SigningKey!.Value.ShouldBe("signkey-prod-00000003");
        fromVariables.SigningKeyFallback!.Value.ShouldBe("signkey-prod-00000004");

        var fromOptions = Create(
            section,
            options =>
            {
                options.EventKey = "event-option";
                options.SigningKey = SigningKeyValue;
                options.SigningKeyFallback = FallbackKeyValue;
            },
            variables);
        fromOptions.EventKey.ShouldBe("event-option");
        fromOptions.SigningKey!.Value.ShouldBe(SigningKeyValue);
        fromOptions.SigningKeyFallback!.Value.ShouldBe(FallbackKeyValue);
    }

    [Fact]
    public void Blank_variables_do_not_hide_the_section()
    {
        var settings = Create(
            [("EventKey", "event-section"), ("SigningKey", SigningKeyValue)],
            variables: [("INNGEST_EVENT_KEY", "  "), ("INNGEST_SIGNING_KEY", "")]);

        settings.EventKey.ShouldBe("event-section");
        settings.SigningKey!.Value.ShouldBe(SigningKeyValue);
    }

    [Fact]
    public void The_app_version_comes_from_the_options_and_then_the_section()
    {
        Create([("AppVersion", "1.0.0")]).AppVersion.ShouldBe("1.0.0");
        Create([("AppVersion", "1.0.0")], options => options.AppVersion = " 2.0.0 ").AppVersion.ShouldBe("2.0.0");
        Create([("AppVersion", "1.0.0")], options => options.AppVersion = "").AppVersion.ShouldBe("1.0.0");
    }

    [Fact]
    public void INNGEST_DEV_wins_over_IsDev_in_the_section()
    {
        Create([("IsDev", "true")], variables: [("INNGEST_DEV", "0")]).IsDev.ShouldBeFalse();
        Create([("IsDev", "false")], variables: [("INNGEST_DEV", "1")]).IsDev.ShouldBeTrue();
        Create([("IsDev", "false")], variables: [("INNGEST_DEV", "http://localhost:3000")]).IsDev.ShouldBeTrue();
        Create([("IsDev", "true")], variables: [("INNGEST_DEV", " ")]).IsDev.ShouldBeTrue();
    }

    [Fact]
    public void IsDev_in_the_options_wins_over_INNGEST_DEV_and_the_section()
    {
        Create([("IsDev", "true")], options => options.IsDev = false, [("INNGEST_DEV", "1")]).IsDev.ShouldBeFalse();
        Create([("IsDev", "false")], options => options.IsDev = true, [("INNGEST_DEV", "0")]).IsDev.ShouldBeTrue();
    }

    [Fact]
    public void The_dev_server_comes_from_the_options_then_INNGEST_DEV_then_the_section()
    {
        (string, string?)[] section = [("IsDev", "true"), ("DevServerUrl", "http://section-box:1111")];
        (string, string?)[] variables = [("INNGEST_DEV", "http://env-box:2222")];

        var fromOptions = Create(section, options => options.DevServerUrl = new Uri("http://code-box:3333"), variables);
        fromOptions.ApiBaseUrl.Host.ShouldBe("code-box");
        fromOptions.EventApiBaseUrl.Host.ShouldBe("code-box");

        var fromVariable = Create(section, variables: variables);
        fromVariable.ApiBaseUrl.Host.ShouldBe("env-box");
        fromVariable.EventApiBaseUrl.Host.ShouldBe("env-box");

        var fromSection = Create(section);
        fromSection.ApiBaseUrl.Host.ShouldBe("section-box");
        fromSection.EventApiBaseUrl.Host.ShouldBe("section-box");
    }

    [Fact]
    public void The_dev_server_of_the_section_only_applies_in_dev_mode()
    {
        var cloud = Create([("DevServerUrl", "http://section-box:1111")]);
        cloud.IsDev.ShouldBeFalse();
        cloud.ApiBaseUrl.Host.ShouldBe("api.inngest.com");
        cloud.EventApiBaseUrl.Host.ShouldBe("inn.gs");

        var variableMakesDev = Create([("DevServerUrl", "http://section-box:1111")], variables: [("INNGEST_DEV", "1")]);
        variableMakesDev.ApiBaseUrl.Host.ShouldBe("section-box");

        var optionMakesCloud = Create([("IsDev", "true"), ("DevServerUrl", "http://section-box:1111")], options => options.IsDev = false);
        optionMakesCloud.ApiBaseUrl.Host.ShouldBe("api.inngest.com");
    }

    [Fact]
    public void An_address_in_the_section_wins_over_its_dev_server()
    {
        var settings = Create([("IsDev", "true"), ("DevServerUrl", "http://section-box:1111"), ("BaseUrl", "https://self-hosted.example.com")]);

        settings.ApiBaseUrl.Host.ShouldBe("self-hosted.example.com");
        settings.EventApiBaseUrl.Host.ShouldBe("self-hosted.example.com");
    }

    [Fact]
    public void A_dev_server_from_code_or_INNGEST_DEV_wins_over_the_addresses_in_the_section()
    {
        (string, string?)[] section = [("BaseUrl", "https://self-hosted.example.com"), ("DevServerUrl", "http://section-box:1111")];

        var fromVariable = Create(section, variables: [("INNGEST_DEV", "http://env-box:2222")]);
        var fromOptions = Create(section, options => options.DevServerUrl = new Uri("http://code-box:3333"), [("INNGEST_DEV", "1")]);

        fromVariable.ApiBaseUrl.Host.ShouldBe("env-box");
        fromVariable.EventApiBaseUrl.Host.ShouldBe("env-box");
        fromOptions.ApiBaseUrl.Host.ShouldBe("code-box");
        fromOptions.EventApiBaseUrl.Host.ShouldBe("code-box");
    }

    [Fact]
    public void The_API_address_comes_from_the_first_of_these_that_is_set()
    {
        (string, string?)[] section =
        [
            ("BaseUrl", "https://base-section.example.com"),
            ("ApiBaseUrl", "https://api-section.example.com"),
            ("IsDev", "true"),
            ("DevServerUrl", "https://dev-section.example.com"),
        ];
        (string, string?)[] variables =
        [
            ("INNGEST_API_BASE_URL", "https://api-var.example.com"),
            ("INNGEST_BASE_URL", "https://base-var.example.com"),
        ];

        Create(section, options => options.ApiBaseUrl = new Uri("https://api-option.example.com"), variables).ApiBaseUrl.Host.ShouldBe("api-option.example.com");
        Create(section, variables: variables).ApiBaseUrl.Host.ShouldBe("api-var.example.com");
        Create(section, variables: variables[1..]).ApiBaseUrl.Host.ShouldBe("base-var.example.com");
        Create(section).ApiBaseUrl.Host.ShouldBe("base-section.example.com");
        Create(section[1..]).ApiBaseUrl.Host.ShouldBe("api-section.example.com");
        Create(section[2..]).ApiBaseUrl.Host.ShouldBe("dev-section.example.com");
        Create(section[2..3]).ApiBaseUrl.Host.ShouldBe("localhost");
    }

    [Fact]
    public void The_event_API_address_comes_from_the_first_of_these_that_is_set()
    {
        (string, string?)[] section =
        [
            ("BaseUrl", "https://base-section.example.com"),
            ("EventApiBaseUrl", "https://events-section.example.com"),
            ("IsDev", "true"),
            ("DevServerUrl", "https://dev-section.example.com"),
        ];
        (string, string?)[] variables =
        [
            ("INNGEST_EVENT_API_BASE_URL", "https://events-var.example.com"),
            ("INNGEST_BASE_URL", "https://base-var.example.com"),
        ];

        Create(section, options => options.EventApiBaseUrl = new Uri("https://events-option.example.com"), variables).EventApiBaseUrl.Host.ShouldBe("events-option.example.com");
        Create(section, variables: variables).EventApiBaseUrl.Host.ShouldBe("events-var.example.com");
        Create(section, variables: variables[1..]).EventApiBaseUrl.Host.ShouldBe("base-var.example.com");
        Create(section).EventApiBaseUrl.Host.ShouldBe("base-section.example.com");
        Create(section[1..]).EventApiBaseUrl.Host.ShouldBe("events-section.example.com");
        Create(section[2..]).EventApiBaseUrl.Host.ShouldBe("dev-section.example.com");
        Create(section[2..3]).EventApiBaseUrl.Host.ShouldBe("localhost");
    }

    [Fact]
    public void An_address_variable_wins_over_the_BaseUrl_of_the_section()
    {
        var settings = Create(
            [("BaseUrl", "https://base-section.example.com")],
            variables: [("INNGEST_API_BASE_URL", "https://api-var.example.com")]);

        settings.ApiBaseUrl.Host.ShouldBe("api-var.example.com");
        settings.EventApiBaseUrl.Host.ShouldBe("base-section.example.com");
    }

    [Fact]
    public void The_serve_origin_comes_from_the_options_then_the_variables_then_the_section()
    {
        (string, string?)[] section = [("ServeOrigin", "https://section.example.com")];

        Create(section, options => options.ServeOrigin = new Uri("https://code.example.com"), [("INNGEST_SERVE_ORIGIN", "https://env.example.com")])
            .ServeOrigin.ShouldBe(new Uri("https://code.example.com"));
        Create(section, variables: [("INNGEST_SERVE_ORIGIN", "https://env.example.com")]).ServeOrigin.ShouldBe(new Uri("https://env.example.com"));
        Create(section, variables: [("INNGEST_SERVE_HOST", "https://old.example.com")]).ServeOrigin.ShouldBe(new Uri("https://old.example.com"));
        Create(section).ServeOrigin.ShouldBe(new Uri("https://section.example.com"));
    }

    [Fact]
    public void The_serve_path_comes_from_the_options_then_the_variables_then_the_section()
    {
        (string, string?)[] section = [("ServePath", "/section")];

        Create(section, options => options.ServePath = "/code", [("INNGEST_SERVE_PATH", "/env")]).ServePath.ShouldBe("/code");
        Create(section, variables: [("INNGEST_SERVE_PATH", "env")]).ServePath.ShouldBe("/env");
        Create(section).ServePath.ShouldBe("/section");
    }

    [Fact]
    public void The_sync_settings_come_from_the_options_then_the_variables_then_the_section()
    {
        (string, string?)[] section = [("AllowInBandSync", "false"), ("AllowUnsignedSync", "false")];
        (string, string?)[] variables = [("INNGEST_ALLOW_IN_BAND_SYNC", "true"), ("INNGEST_ENABLE_UNAUTHED_SYNC", "true")];

        var fromSection = Create(section);
        fromSection.AllowInBandSync.ShouldBeFalse();
        fromSection.AllowUnsignedSync.ShouldBeFalse();

        var fromVariables = Create(section, variables: variables);
        fromVariables.AllowInBandSync.ShouldBeTrue();
        fromVariables.AllowUnsignedSync.ShouldBeTrue();

        var fromOptions = Create(
            [("AllowInBandSync", "true"), ("AllowUnsignedSync", "true")],
            options =>
            {
                options.AllowInBandSync = false;
                options.AllowUnsignedSync = false;
            },
            variables);
        fromOptions.AllowInBandSync.ShouldBeFalse();
        fromOptions.AllowUnsignedSync.ShouldBeFalse();
    }

    [Fact]
    public void A_sync_variable_that_is_no_flag_does_not_hide_the_section() =>
        Create([("AllowInBandSync", "false")], variables: [("INNGEST_ALLOW_IN_BAND_SYNC", "garbage")]).AllowInBandSync.ShouldBeFalse();

    [Fact]
    public void The_environment_comes_from_the_options_then_INNGEST_ENV_then_the_section_then_the_branch_variables()
    {
        (string, string?)[] section = [("Environment", "section-env")];

        Create(section, options => options.Environment = "code-env", [("INNGEST_ENV", "env-env"), ("BRANCH_NAME", "branch")]).Environment.ShouldBe("code-env");
        Create(section, variables: [("INNGEST_ENV", "env-env"), ("BRANCH_NAME", "branch")]).Environment.ShouldBe("env-env");
        Create(section, variables: [("INNGEST_ENV", " "), ("BRANCH_NAME", "branch")]).Environment.ShouldBe("section-env");
        Create([("Unknown", "value")], variables: [("BRANCH_NAME", "branch")]).Environment.ShouldBe("branch");
    }

    [Theory]
    [InlineData("BRANCH_NAME")]
    [InlineData("VERCEL_GIT_COMMIT_REF")]
    [InlineData("BRANCH")]
    [InlineData("CF_PAGES_BRANCH")]
    [InlineData("RENDER_GIT_BRANCH")]
    [InlineData("RAILWAY_GIT_BRANCH")]
    public void A_configured_environment_wins_over_the_branch_variables_of_hosting_platforms(string name) =>
        Create([("Environment", "section-env")], variables: [(name, "branch")]).Environment.ShouldBe("section-env");

    [Fact]
    public void A_value_the_section_has_wrong_fails_naming_its_key()
    {
        Should.Throw<InvalidOperationException>(() => Create([("IsDev", "maybe")]))
            .Message.ShouldBe("Inngest:IsDev must be true or false, but it is 'maybe'.");
        Should.Throw<InvalidOperationException>(() => Create([("BaseUrl", "ftp://files.example.com")]))
            .Message.ShouldBe("Inngest:BaseUrl must be an absolute http or https URL, but it is 'ftp://files.example.com'.");
    }

    // A typo should not wait in the file until the option or variable that hides it is removed.
    [Fact]
    public void A_value_the_section_has_wrong_fails_even_when_something_above_it_wins() =>
        Should.Throw<InvalidOperationException>(() => Create([("IsDev", "maybe")], options => options.IsDev = true, [("INNGEST_DEV", "1")]))
            .Message.ShouldContain("Inngest:IsDev");

    [Fact]
    public void Printing_the_settings_does_not_reveal_keys_from_the_section()
    {
        var settings = Create([("EventKey", "secret-event-key"), ("SigningKey", SigningKeyValue), ("SigningKeyFallback", FallbackKeyValue)]);

        var text = settings.ToString();

        text.ShouldNotContain("secret-event-key");
        text.ShouldNotContain("12345678");
        text.ShouldNotContain("abcdef01");
    }
}
