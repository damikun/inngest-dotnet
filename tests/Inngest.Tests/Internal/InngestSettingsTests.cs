using Inngest.Configuration;

namespace Inngest.Tests.Internal;

public sealed class InngestSettingsTests
{
    private static InngestSettings Create(Action<InngestOptions>? configure = null, params (string Name, string? Value)[] variables)
    {
        var options = new InngestOptions { AppId = "shop" };
        configure?.Invoke(options);
        return InngestSettings.Create(options, name => variables.FirstOrDefault(v => v.Name == name).Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AppId_is_required(string? appId)
    {
        var exception = Should.Throw<InvalidOperationException>(() =>
            InngestSettings.Create(new InngestOptions { AppId = appId }, _ => null));

        exception.Message.ShouldContain("InngestOptions.AppId");
    }

    [Fact]
    public void App_id_and_version_are_taken_from_the_options()
    {
        var settings = Create(options => options.AppVersion = " 1.4.0 ");

        settings.AppId.ShouldBe("shop");
        settings.AppVersion.ShouldBe("1.4.0");
    }

    [Fact]
    public void Defaults_target_inngest_cloud()
    {
        var settings = Create();

        settings.Mode.ShouldBe(InngestMode.Cloud);
        settings.IsDev.ShouldBeFalse();
        settings.ApiBaseUrl.AbsoluteUri.ShouldBe("https://api.inngest.com/");
        settings.EventApiBaseUrl.AbsoluteUri.ShouldBe("https://inn.gs/");
        settings.EventKey.ShouldBeNull();
        settings.SigningKey.ShouldBeNull();
        settings.SigningKeyFallback.ShouldBeNull();
        settings.Environment.ShouldBeNull();
        settings.ServeOrigin.ShouldBeNull();
        settings.ServePath.ShouldBeNull();
        settings.AllowInBandSync.ShouldBeTrue();
    }

    [Fact]
    public void Printing_the_settings_does_not_reveal_the_keys()
    {
        var settings = Create(options =>
        {
            options.EventKey = "secret-event-key";
            options.SigningKey = "signkey-prod-12345678";
            options.SigningKeyFallback = "signkey-prod-abcdef01";
        });

        var text = settings.ToString();

        text.ShouldContain("AppId = shop");
        text.ShouldNotContain("secret-event-key");
        text.ShouldNotContain("12345678");
        text.ShouldNotContain("abcdef01");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("True")]
    [InlineData("  true  ")]
    public void Truthy_INNGEST_DEV_selects_dev_mode_with_the_local_dev_server(string value)
    {
        var settings = Create(null, ("INNGEST_DEV", value));

        settings.Mode.ShouldBe(InngestMode.Dev);
        settings.ApiBaseUrl.AbsoluteUri.ShouldBe("http://localhost:8288/");
        settings.EventApiBaseUrl.AbsoluteUri.ShouldBe("http://localhost:8288/");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("FALSE")]
    public void Falsy_INNGEST_DEV_selects_cloud_mode(string value)
    {
        var settings = Create(null, ("INNGEST_DEV", value));

        settings.Mode.ShouldBe(InngestMode.Cloud);
        settings.ApiBaseUrl.AbsoluteUri.ShouldBe("https://api.inngest.com/");
    }

    [Theory]
    [InlineData("http://localhost:3000", "http://localhost:3000/")]
    [InlineData("https://dev.example.com:8288/", "https://dev.example.com:8288/")]
    [InlineData("0.0.0.0:8288", "http://0.0.0.0:8288/")]
    [InlineData("localhost:9000", "http://localhost:9000/")]
    [InlineData("inngest-dev", "http://inngest-dev/")]
    public void A_URL_in_INNGEST_DEV_selects_dev_mode_and_is_the_address_of_both_APIs(string value, string expected)
    {
        var settings = Create(null, ("INNGEST_DEV", value));

        settings.Mode.ShouldBe(InngestMode.Dev);
        settings.ApiBaseUrl.AbsoluteUri.ShouldBe(expected);
        settings.EventApiBaseUrl.AbsoluteUri.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("undefined")]
    public void A_blank_INNGEST_DEV_is_ignored(string? value) =>
        Create(null, ("INNGEST_DEV", value)).Mode.ShouldBe(InngestMode.Cloud);

    [Theory]
    [InlineData("http://")]
    [InlineData("ftp://files.example.com")]
    [InlineData("not a url")]
    public void An_INNGEST_DEV_that_is_neither_a_flag_nor_a_URL_is_rejected(string value)
    {
        var exception = Should.Throw<InvalidOperationException>(() => Create(null, ("INNGEST_DEV", value)));

        exception.Message.ShouldContain("INNGEST_DEV");
    }

    [Fact]
    public void The_IsDev_option_wins_over_INNGEST_DEV()
    {
        Create(options => options.IsDev = false, ("INNGEST_DEV", "1")).Mode.ShouldBe(InngestMode.Cloud);
        Create(options => options.IsDev = true, ("INNGEST_DEV", "0")).Mode.ShouldBe(InngestMode.Dev);
        Create(options => options.IsDev = true).Mode.ShouldBe(InngestMode.Dev);
    }

    [Fact]
    public void The_dev_server_URL_of_INNGEST_DEV_does_not_redirect_cloud_mode()
    {
        var settings = Create(options => options.IsDev = false, ("INNGEST_DEV", "http://localhost:3000"));

        settings.Mode.ShouldBe(InngestMode.Cloud);
        settings.ApiBaseUrl.AbsoluteUri.ShouldBe("https://api.inngest.com/");
        settings.EventApiBaseUrl.AbsoluteUri.ShouldBe("https://inn.gs/");
    }

    [Fact]
    public void The_DevServerUrl_option_is_the_dev_server_in_dev_mode_and_wins_over_INNGEST_DEV()
    {
        var settings = Create(
            options =>
            {
                options.IsDev = true;
                options.DevServerUrl = new Uri("http://dev-box:9999");
            },
            ("INNGEST_DEV", "http://localhost:3000"));

        settings.ApiBaseUrl.AbsoluteUri.ShouldBe("http://dev-box:9999/");
        settings.EventApiBaseUrl.AbsoluteUri.ShouldBe("http://dev-box:9999/");
    }

    [Fact]
    public void The_DevServerUrl_option_is_ignored_in_cloud_mode()
    {
        var settings = Create(options => options.DevServerUrl = new Uri("http://dev-box:9999"));

        settings.Mode.ShouldBe(InngestMode.Cloud);
        settings.ApiBaseUrl.AbsoluteUri.ShouldBe("https://api.inngest.com/");
    }

    [Fact]
    public void The_API_address_comes_from_the_first_of_these_that_is_set()
    {
        var options = new Action<InngestOptions>(o =>
        {
            o.BaseUrl = new Uri("https://base-option.example.com");
            o.ApiBaseUrl = new Uri("https://api-option.example.com");
        });
        var variables = new (string, string?)[]
        {
            ("INNGEST_API_BASE_URL", "https://api-var.example.com"),
            ("INNGEST_BASE_URL", "https://base-var.example.com"),
            ("INNGEST_DEV", "https://dev-var.example.com"),
        };

        Create(options, variables).ApiBaseUrl.Host.ShouldBe("base-option.example.com");
        Create(o => o.ApiBaseUrl = new Uri("https://api-option.example.com"), variables).ApiBaseUrl.Host.ShouldBe("api-option.example.com");
        Create(null, variables).ApiBaseUrl.Host.ShouldBe("api-var.example.com");
        Create(null, variables[1..]).ApiBaseUrl.Host.ShouldBe("base-var.example.com");
        Create(null, variables[2..]).ApiBaseUrl.Host.ShouldBe("dev-var.example.com");
        Create(null).ApiBaseUrl.Host.ShouldBe("api.inngest.com");
    }

    [Fact]
    public void The_event_API_address_comes_from_the_first_of_these_that_is_set()
    {
        var options = new Action<InngestOptions>(o =>
        {
            o.BaseUrl = new Uri("https://base-option.example.com");
            o.EventApiBaseUrl = new Uri("https://events-option.example.com");
        });
        var variables = new (string, string?)[]
        {
            ("INNGEST_EVENT_API_BASE_URL", "https://events-var.example.com"),
            ("INNGEST_BASE_URL", "https://base-var.example.com"),
            ("INNGEST_DEV", "https://dev-var.example.com"),
        };

        Create(options, variables).EventApiBaseUrl.Host.ShouldBe("base-option.example.com");
        Create(o => o.EventApiBaseUrl = new Uri("https://events-option.example.com"), variables).EventApiBaseUrl.Host.ShouldBe("events-option.example.com");
        Create(null, variables).EventApiBaseUrl.Host.ShouldBe("events-var.example.com");
        Create(null, variables[1..]).EventApiBaseUrl.Host.ShouldBe("base-var.example.com");
        Create(null, variables[2..]).EventApiBaseUrl.Host.ShouldBe("dev-var.example.com");
        Create(null).EventApiBaseUrl.Host.ShouldBe("inn.gs");
    }

    [Fact]
    public void The_API_and_event_API_addresses_are_independent()
    {
        var settings = Create(
            null,
            ("INNGEST_API_BASE_URL", "https://api.example.com"),
            ("INNGEST_EVENT_API_BASE_URL", "https://events.example.com"));

        settings.ApiBaseUrl.Host.ShouldBe("api.example.com");
        settings.EventApiBaseUrl.Host.ShouldBe("events.example.com");
    }

    [Fact]
    public void Dev_mode_falls_back_to_the_local_dev_server_for_both()
    {
        var settings = Create(options => options.IsDev = true);

        settings.ApiBaseUrl.AbsoluteUri.ShouldBe("http://localhost:8288/");
        settings.EventApiBaseUrl.AbsoluteUri.ShouldBe("http://localhost:8288/");
    }

    [Fact]
    public void Explicit_addresses_apply_in_dev_mode_too()
    {
        var settings = Create(options => options.IsDev = true, ("INNGEST_BASE_URL", "http://self-hosted:8288"));

        settings.ApiBaseUrl.AbsoluteUri.ShouldBe("http://self-hosted:8288/");
        settings.EventApiBaseUrl.AbsoluteUri.ShouldBe("http://self-hosted:8288/");
    }

    [Fact]
    public void Addresses_in_variables_may_leave_out_the_scheme()
    {
        var settings = Create(null, ("INNGEST_BASE_URL", "inngest.internal:8288"));

        settings.ApiBaseUrl.AbsoluteUri.ShouldBe("http://inngest.internal:8288/");
    }

    [Fact]
    public void Paths_in_addresses_are_kept()
    {
        var settings = Create(options => options.BaseUrl = new Uri("https://gateway.example.com/inngest/"));

        settings.ApiBaseUrl.AbsoluteUri.ShouldBe("https://gateway.example.com/inngest/");
        settings.EventApiBaseUrl.AbsoluteUri.ShouldBe("https://gateway.example.com/inngest/");
    }

    [Theory]
    [InlineData("INNGEST_API_BASE_URL")]
    [InlineData("INNGEST_BASE_URL")]
    public void An_address_variable_that_is_not_a_URL_is_rejected_by_name(string name)
    {
        var exception = Should.Throw<InvalidOperationException>(() => Create(null, (name, "http://")));

        exception.Message.ShouldContain(name);
    }

    [Fact]
    public void An_address_variable_that_is_not_needed_is_not_validated()
    {
        var settings = Create(options => options.BaseUrl = new Uri("https://base.example.com"), ("INNGEST_API_BASE_URL", "http://"));

        settings.ApiBaseUrl.Host.ShouldBe("base.example.com");
    }

    [Theory]
    [InlineData("BaseUrl")]
    [InlineData("ApiBaseUrl")]
    [InlineData("EventApiBaseUrl")]
    [InlineData("DevServerUrl")]
    [InlineData("ServeOrigin")]
    public void An_address_option_must_be_an_absolute_http_URL(string option)
    {
        void Configure(InngestOptions options, Uri value)
        {
            options.IsDev = true;
            switch (option)
            {
                case "BaseUrl": options.BaseUrl = value; break;
                case "ApiBaseUrl": options.ApiBaseUrl = value; break;
                case "EventApiBaseUrl": options.EventApiBaseUrl = value; break;
                case "DevServerUrl": options.DevServerUrl = value; break;
                default: options.ServeOrigin = value; break;
            }
        }

        foreach (var invalid in new[] { new Uri("/relative", UriKind.Relative), new Uri("ftp://files.example.com") })
        {
            var exception = Should.Throw<InvalidOperationException>(() => Create(options => Configure(options, invalid)));
            exception.Message.ShouldContain($"InngestOptions.{option}");
        }
    }

    private const string SigningKeyValue = "signkey-prod-12345678";
    private const string FallbackKeyValue = "signkey-prod-abcdef01";

    [Fact]
    public void Keys_come_from_the_options()
    {
        var settings = Create(options =>
        {
            options.EventKey = "event-key";
            options.SigningKey = SigningKeyValue;
            options.SigningKeyFallback = FallbackKeyValue;
        });

        settings.EventKey.ShouldBe("event-key");
        settings.SigningKey!.Value.ShouldBe(SigningKeyValue);
        settings.SigningKey.BearerToken.ShouldBe("signkey-prod-b2ed992186a5cb19f6668aade821f502c1d00970dfd0e35128d51bac4649916c");
        settings.SigningKeyFallback!.Value.ShouldBe(FallbackKeyValue);
    }

    [Fact]
    public void Keys_come_from_variables_when_the_options_do_not_set_them()
    {
        var settings = Create(
            null,
            ("INNGEST_EVENT_KEY", "  event-from-env\n"),
            ("INNGEST_SIGNING_KEY", SigningKeyValue),
            ("INNGEST_SIGNING_KEY_FALLBACK", FallbackKeyValue));

        settings.EventKey.ShouldBe("event-from-env");
        settings.SigningKey!.Value.ShouldBe(SigningKeyValue);
        settings.SigningKeyFallback!.Value.ShouldBe(FallbackKeyValue);
    }

    [Fact]
    public void Keys_in_the_options_win_over_variables()
    {
        var settings = Create(
            options =>
            {
                options.EventKey = "event-option";
                options.SigningKey = SigningKeyValue;
                options.SigningKeyFallback = FallbackKeyValue;
            },
            ("INNGEST_EVENT_KEY", "event-env"),
            ("INNGEST_SIGNING_KEY", "signkey-prod-ffffffff"),
            ("INNGEST_SIGNING_KEY_FALLBACK", "signkey-prod-eeeeeeee"));

        settings.EventKey.ShouldBe("event-option");
        settings.SigningKey!.Value.ShouldBe(SigningKeyValue);
        settings.SigningKeyFallback!.Value.ShouldBe(FallbackKeyValue);
    }

    [Fact]
    public void Blank_options_fall_through_to_variables()
    {
        var settings = Create(
            options =>
            {
                options.EventKey = "";
                options.SigningKey = "  ";
            },
            ("INNGEST_EVENT_KEY", "event-env"),
            ("INNGEST_SIGNING_KEY", SigningKeyValue));

        settings.EventKey.ShouldBe("event-env");
        settings.SigningKey!.Value.ShouldBe(SigningKeyValue);
    }

    [Fact]
    public void Blank_variables_leave_the_keys_unset()
    {
        var settings = Create(null, ("INNGEST_EVENT_KEY", " "), ("INNGEST_SIGNING_KEY", ""), ("INNGEST_SIGNING_KEY_FALLBACK", null));

        settings.EventKey.ShouldBeNull();
        settings.SigningKey.ShouldBeNull();
        settings.SigningKeyFallback.ShouldBeNull();
    }

    private static readonly string[] Names =
    [
        "INNGEST_ENV",
        "BRANCH_NAME",
        "VERCEL_GIT_COMMIT_REF",
        "BRANCH",
        "CF_PAGES_BRANCH",
        "RENDER_GIT_BRANCH",
        "RAILWAY_GIT_BRANCH",
    ];

    [Fact]
    public void The_first_branch_variable_that_is_set_names_the_environment()
    {
        for (var i = 0; i < Names.Length; i++)
        {
            var variables = Names.Skip(i).Select(name => (name, (string?)$"from-{name}")).ToArray();

            Create(null, variables).Environment.ShouldBe($"from-{Names[i]}");
        }
    }

    [Fact]
    public void Blank_branch_variables_are_skipped()
    {
        var settings = Create(null, ("INNGEST_ENV", ""), ("BRANCH_NAME", "  "), ("BRANCH", "preview"));

        settings.Environment.ShouldBe("preview");
    }

    [Fact]
    public void The_environment_option_wins_over_every_variable()
    {
        var variables = Names.Select(name => (name, (string?)"from-env")).ToArray();

        Create(options => options.Environment = "staging", variables).Environment.ShouldBe("staging");
    }

    [Fact]
    public void There_is_no_environment_by_default() => Create().Environment.ShouldBeNull();

    [Fact]
    public void Serve_origin_and_path_come_from_the_options()
    {
        var settings = Create(options =>
        {
            options.ServeOrigin = new Uri("https://shop.example.com");
            options.ServePath = "/hooks/inngest";
        });

        settings.ServeOrigin.ShouldBe(new Uri("https://shop.example.com"));
        settings.ServePath.ShouldBe("/hooks/inngest");
    }

    [Fact]
    public void Serve_origin_and_path_come_from_variables()
    {
        var settings = Create(null, ("INNGEST_SERVE_ORIGIN", "https://shop.example.com"), ("INNGEST_SERVE_PATH", "/hooks/inngest"));

        settings.ServeOrigin.ShouldBe(new Uri("https://shop.example.com"));
        settings.ServePath.ShouldBe("/hooks/inngest");
    }

    [Fact]
    public void The_deprecated_serve_host_is_used_when_there_is_no_origin()
    {
        Create(null, ("INNGEST_SERVE_HOST", "https://old.example.com")).ServeOrigin.ShouldBe(new Uri("https://old.example.com"));
        Create(null, ("INNGEST_SERVE_HOST", "https://old.example.com"), ("INNGEST_SERVE_ORIGIN", "https://new.example.com"))
            .ServeOrigin.ShouldBe(new Uri("https://new.example.com"));
    }

    [Fact]
    public void Serve_options_win_over_variables()
    {
        var settings = Create(
            options =>
            {
                options.ServeOrigin = new Uri("https://option.example.com");
                options.ServePath = "/option";
            },
            ("INNGEST_SERVE_ORIGIN", "https://env.example.com"),
            ("INNGEST_SERVE_PATH", "/env"));

        settings.ServeOrigin.ShouldBe(new Uri("https://option.example.com"));
        settings.ServePath.ShouldBe("/option");
    }

    [Fact]
    public void The_serve_path_gets_a_leading_slash()
    {
        Create(options => options.ServePath = "api/inngest").ServePath.ShouldBe("/api/inngest");
        Create(null, ("INNGEST_SERVE_PATH", "api/inngest")).ServePath.ShouldBe("/api/inngest");
    }

    [Fact]
    public void A_serve_origin_variable_without_a_scheme_gets_http()
    {
        Create(null, ("INNGEST_SERVE_ORIGIN", "app.internal:5000")).ServeOrigin.ShouldBe(new Uri("http://app.internal:5000"));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("garbage", true)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("0", false)]
    public void The_variable_can_turn_in_band_sync_off(string? value, bool expected) =>
        Create(null, ("INNGEST_ALLOW_IN_BAND_SYNC", value)).AllowInBandSync.ShouldBe(expected);

    [Fact]
    public void The_in_band_sync_option_turns_it_off_whatever_the_variable_says() =>
        Create(options => options.AllowInBandSync = false, ("INNGEST_ALLOW_IN_BAND_SYNC", "true")).AllowInBandSync.ShouldBeFalse();

    [Fact]
    public void The_in_band_sync_option_turns_it_on_whatever_the_variable_says() =>
        Create(options => options.AllowInBandSync = true, ("INNGEST_ALLOW_IN_BAND_SYNC", "false")).AllowInBandSync.ShouldBeTrue();

    [Fact]
    public void Unsigned_syncs_are_allowed_unless_turned_off()
    {
        Create(null).AllowUnsignedSync.ShouldBeTrue();
        Create(null, ("INNGEST_ENABLE_UNAUTHED_SYNC", "false")).AllowUnsignedSync.ShouldBeFalse();
        Create(options => options.AllowUnsignedSync = true, ("INNGEST_ENABLE_UNAUTHED_SYNC", "false")).AllowUnsignedSync.ShouldBeTrue();
    }

    [Fact]
    public void Asks_for_each_variable_by_its_documented_name()
    {
        var asked = new List<string>();

        InngestSettings.Create(new InngestOptions { AppId = "shop" }, name =>
        {
            asked.Add(name);
            return null;
        });

        asked.ShouldContain("INNGEST_DEV");
        asked.ShouldContain("INNGEST_EVENT_KEY");
        asked.ShouldContain("INNGEST_SIGNING_KEY");
        asked.ShouldContain("INNGEST_SIGNING_KEY_FALLBACK");
        asked.ShouldContain("INNGEST_ENV");
        asked.ShouldContain("INNGEST_API_BASE_URL");
        asked.ShouldContain("INNGEST_EVENT_API_BASE_URL");
        asked.ShouldContain("INNGEST_BASE_URL");
        asked.ShouldContain("INNGEST_SERVE_ORIGIN");
        asked.ShouldContain("INNGEST_SERVE_PATH");
        asked.ShouldContain("INNGEST_ALLOW_IN_BAND_SYNC");
    }

    [Fact]
    public void Options_must_not_be_null() =>
        Should.Throw<ArgumentNullException>(() => InngestSettings.Create(null!, _ => null));

    [Fact]
    public void A_variable_reader_must_not_be_null() =>
        Should.Throw<ArgumentNullException>(() => InngestSettings.Create(new InngestOptions { AppId = "shop" }, null!));
}
