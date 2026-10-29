using Inngest.Internal;
using Microsoft.Extensions.Configuration;

namespace Inngest.Tests.Internal;

public sealed class InngestConfigurationTests
{
    private static InngestOptions Read(params (string Key, string? Value)[] keys) => InngestConfiguration.Read(InngestSection.Of(keys));

    private static string? Text(InngestOptions options, string key) => key switch
    {
        "AppId" => options.AppId,
        "AppVersion" => options.AppVersion,
        "EventKey" => options.EventKey,
        "SigningKey" => options.SigningKey,
        "SigningKeyFallback" => options.SigningKeyFallback,
        "Environment" => options.Environment,
        "ServePath" => options.ServePath,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, null),
    };

    [Fact]
    public void Every_key_is_read_into_the_option_of_the_same_name()
    {
        var options = Read(
            ("AppId", "billing"),
            ("AppVersion", "2026.10.3"),
            ("EventKey", "event-key"),
            ("SigningKey", "signkey-prod-12345678"),
            ("SigningKeyFallback", "signkey-prod-abcdef01"),
            ("Environment", "staging"),
            ("IsDev", "true"),
            ("DevServerUrl", "http://localhost:8288"),
            ("BaseUrl", "https://inngest.example.com"),
            ("ApiBaseUrl", "https://api.example.com"),
            ("EventApiBaseUrl", "https://events.example.com"),
            ("ServeOrigin", "https://shop.example.com"),
            ("ServePath", "/api/inngest"),
            ("AllowInBandSync", "false"),
            ("AllowUnsignedSync", "false"),
            ("Checkpointing:Enabled", "true"),
            ("Checkpointing:MaxRuntime", "30s"),
            ("Checkpointing:BufferedSteps", "5"),
            ("Checkpointing:MaxInterval", "2s"));

        options.AppId.ShouldBe("billing");
        options.AppVersion.ShouldBe("2026.10.3");
        options.EventKey.ShouldBe("event-key");
        options.SigningKey.ShouldBe("signkey-prod-12345678");
        options.SigningKeyFallback.ShouldBe("signkey-prod-abcdef01");
        options.Environment.ShouldBe("staging");
        options.IsDev.ShouldBe(true);
        options.DevServerUrl.ShouldBe(new Uri("http://localhost:8288"));
        options.BaseUrl.ShouldBe(new Uri("https://inngest.example.com"));
        options.ApiBaseUrl.ShouldBe(new Uri("https://api.example.com"));
        options.EventApiBaseUrl.ShouldBe(new Uri("https://events.example.com"));
        options.ServeOrigin.ShouldBe(new Uri("https://shop.example.com"));
        options.ServePath.ShouldBe("/api/inngest");
        options.AllowInBandSync.ShouldBe(false);
        options.AllowUnsignedSync.ShouldBe(false);
        options.Checkpointing.ShouldBe(new CheckpointingOptions
        {
            Enabled = true,
            MaxRuntime = TimeSpan.FromSeconds(30),
            BufferedSteps = 5,
            MaxInterval = TimeSpan.FromSeconds(2),
        });
    }

    [Fact]
    public void A_section_with_no_known_keys_leaves_everything_unset()
    {
        var options = Read(("Unknown", "value"), ("Checkpointing:Unknown", "1"), ("Nested:AppId", "not-this-one"));

        options.AppId.ShouldBeNull();
        options.AppVersion.ShouldBeNull();
        options.EventKey.ShouldBeNull();
        options.SigningKey.ShouldBeNull();
        options.SigningKeyFallback.ShouldBeNull();
        options.Environment.ShouldBeNull();
        options.IsDev.ShouldBeNull();
        options.DevServerUrl.ShouldBeNull();
        options.BaseUrl.ShouldBeNull();
        options.ApiBaseUrl.ShouldBeNull();
        options.EventApiBaseUrl.ShouldBeNull();
        options.ServeOrigin.ShouldBeNull();
        options.ServePath.ShouldBeNull();
        options.AllowInBandSync.ShouldBeNull();
        options.AllowUnsignedSync.ShouldBeNull();
        options.Checkpointing.ShouldBeNull();
    }

    [Fact]
    public void Keys_are_matched_without_regard_to_case()
    {
        var options = Read(("appid", "billing"), ("EVENTKEY", "event-key"), ("checkpointing:BUFFEREDSTEPS", "3"));

        options.AppId.ShouldBe("billing");
        options.EventKey.ShouldBe("event-key");
        options.Checkpointing!.BufferedSteps.ShouldBe(3);
    }

    [Theory]
    [InlineData("AppId")]
    [InlineData("AppVersion")]
    [InlineData("EventKey")]
    [InlineData("SigningKey")]
    [InlineData("SigningKeyFallback")]
    [InlineData("Environment")]
    [InlineData("ServePath")]
    public void Text_is_trimmed_and_blank_text_is_unset(string key)
    {
        Text(Read((key, "  value\n")), key).ShouldBe("value");
        Text(Read((key, "   ")), key).ShouldBeNull();
        Text(Read((key, "")), key).ShouldBeNull();
        Text(Read((key, null)), key).ShouldBeNull();
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("True", true)]
    [InlineData("1", true)]
    [InlineData("  true  ", true)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("0", false)]
    public void Flags_are_true_false_1_or_0_in_any_case(string value, bool expected)
    {
        var options = Read(("IsDev", value), ("AllowInBandSync", value), ("AllowUnsignedSync", value), ("Checkpointing:Enabled", value));

        options.IsDev.ShouldBe(expected);
        options.AllowInBandSync.ShouldBe(expected);
        options.AllowUnsignedSync.ShouldBe(expected);
        options.Checkpointing!.Enabled.ShouldBe(expected);
    }

    [Theory]
    [InlineData("http://localhost:8288", "http://localhost:8288/")]
    [InlineData("https://inngest.example.com/gateway/", "https://inngest.example.com/gateway/")]
    [InlineData("  https://inngest.example.com  ", "https://inngest.example.com/")]
    [InlineData("HTTPS://Inngest.Example.com", "https://inngest.example.com/")]
    public void Addresses_are_absolute_http_or_https_URLs(string value, string expected)
    {
        var options = Read(("DevServerUrl", value), ("BaseUrl", value), ("ApiBaseUrl", value), ("EventApiBaseUrl", value), ("ServeOrigin", value));

        options.DevServerUrl!.AbsoluteUri.ShouldBe(expected);
        options.BaseUrl!.AbsoluteUri.ShouldBe(expected);
        options.ApiBaseUrl!.AbsoluteUri.ShouldBe(expected);
        options.EventApiBaseUrl!.AbsoluteUri.ShouldBe(expected);
        options.ServeOrigin!.AbsoluteUri.ShouldBe(expected);
    }

    // A bare number is no duration: TimeSpan would read "30" as 30 days, which nobody means.
    [Theory]
    [InlineData("IsDev", "maybe", "Inngest:IsDev must be true or false, but it is 'maybe'.")]
    [InlineData("AllowInBandSync", "yes", "Inngest:AllowInBandSync must be true or false, but it is 'yes'.")]
    [InlineData("AllowUnsignedSync", "2", "Inngest:AllowUnsignedSync must be true or false, but it is '2'.")]
    [InlineData("DevServerUrl", "localhost:8288", "Inngest:DevServerUrl must be an absolute http or https URL, but it is 'localhost:8288'.")]
    [InlineData("BaseUrl", "ftp://files.example.com", "Inngest:BaseUrl must be an absolute http or https URL, but it is 'ftp://files.example.com'.")]
    [InlineData("ApiBaseUrl", "/relative", "Inngest:ApiBaseUrl must be an absolute http or https URL, but it is '/relative'.")]
    [InlineData("EventApiBaseUrl", "http://", "Inngest:EventApiBaseUrl must be an absolute http or https URL, but it is 'http://'.")]
    [InlineData("ServeOrigin", "not a url", "Inngest:ServeOrigin must be an absolute http or https URL, but it is 'not a url'.")]
    [InlineData("Checkpointing:Enabled", "on", "Inngest:Checkpointing:Enabled must be true or false, but it is 'on'.")]
    [InlineData("Checkpointing:BufferedSteps", "many", "Inngest:Checkpointing:BufferedSteps must be a whole number, but it is 'many'.")]
    [InlineData("Checkpointing:BufferedSteps", "1.5", "Inngest:Checkpointing:BufferedSteps must be a whole number, but it is '1.5'.")]
    [InlineData("Checkpointing:BufferedSteps", "99999999999", "Inngest:Checkpointing:BufferedSteps must be a whole number, but it is '99999999999'.")]
    [InlineData("Checkpointing:MaxRuntime", "soon", "Inngest:Checkpointing:MaxRuntime must be a duration such as 30s, 1h30m or 00:00:30, but it is 'soon'.")]
    [InlineData("Checkpointing:MaxRuntime", "1.5s", "Inngest:Checkpointing:MaxRuntime must be a duration such as 30s, 1h30m or 00:00:30, but it is '1.5s'.")]
    [InlineData("Checkpointing:MaxInterval", "25:61:00", "Inngest:Checkpointing:MaxInterval must be a duration such as 30s, 1h30m or 00:00:30, but it is '25:61:00'.")]
    [InlineData("Checkpointing:MaxInterval", "30", "Inngest:Checkpointing:MaxInterval must be a duration such as 30s, 1h30m or 00:00:30, but it is '30'.")]
    public void A_value_that_cannot_be_read_is_rejected_by_its_key(string key, string value, string message) =>
        Should.Throw<InvalidOperationException>(() => Read((key, value))).Message.ShouldBe(message);

    [Fact]
    public void The_key_in_a_message_starts_at_the_root_of_the_configuration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Apps:Billing:Inngest:Checkpointing:BufferedSteps"] = "none" })
            .Build();

        Should.Throw<InvalidOperationException>(() => InngestConfiguration.Read(configuration.GetSection("Apps:Billing:Inngest")))
            .Message.ShouldStartWith("Apps:Billing:Inngest:Checkpointing:BufferedSteps must be");
    }

    [Fact]
    public void A_configuration_that_is_not_a_section_has_the_keys_at_its_top()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AppId"] = "billing", ["Checkpointing:BufferedSteps"] = "4" })
            .Build();

        var options = InngestConfiguration.Read(configuration);

        options.AppId.ShouldBe("billing");
        options.Checkpointing!.BufferedSteps.ShouldBe(4);

        configuration["IsDev"] = "maybe";
        Should.Throw<InvalidOperationException>(() => InngestConfiguration.Read(configuration)).Message.ShouldStartWith("IsDev must be");
    }

    [Fact]
    public void Checkpointing_is_unset_unless_the_subsection_sets_a_key()
    {
        Read().Checkpointing.ShouldBeNull();
        Read(("Checkpointing:Unknown", "1")).Checkpointing.ShouldBeNull();
        Read(("Checkpointing:Enabled", " "), ("Checkpointing:MaxRuntime", "")).Checkpointing.ShouldBeNull();
    }

    [Fact]
    public void Checkpointing_keeps_the_defaults_of_the_keys_the_subsection_leaves_out()
    {
        Read(("Checkpointing:Enabled", "true")).Checkpointing.ShouldBe(new CheckpointingOptions());
        Read(("Checkpointing:Enabled", "false")).Checkpointing.ShouldBe(new CheckpointingOptions { Enabled = false });
        Read(("Checkpointing:BufferedSteps", "4")).Checkpointing.ShouldBe(new CheckpointingOptions { BufferedSteps = 4 });
        Read(("Checkpointing:MaxRuntime", "1m")).Checkpointing.ShouldBe(new CheckpointingOptions { MaxRuntime = TimeSpan.FromMinutes(1) });
        Read(("Checkpointing:MaxInterval", "1s")).Checkpointing.ShouldBe(new CheckpointingOptions { MaxInterval = TimeSpan.FromSeconds(1) });
    }

    [Theory]
    [InlineData("30s", 30)]
    [InlineData("5m", 300)]
    [InlineData("1h30m", 5400)]
    [InlineData("1w", 604800)]
    [InlineData("  30s  ", 30)]
    [InlineData("00:00:30", 30)]
    [InlineData("00:05:00", 300)]
    [InlineData("01:30:00", 5400)]
    [InlineData("1.00:00:00", 86400)]
    [InlineData("00:00:00.5", 0.5)]
    [InlineData("  00:00:30  ", 30)]
    public void Durations_are_Inngest_time_strings_or_the_form_TimeSpan_writes(string value, double seconds)
    {
        var checkpointing = Read(("Checkpointing:MaxRuntime", value), ("Checkpointing:MaxInterval", value)).Checkpointing!;

        checkpointing.MaxRuntime.ShouldBe(TimeSpan.FromSeconds(seconds));
        checkpointing.MaxInterval.ShouldBe(TimeSpan.FromSeconds(seconds));
    }

    [Fact]
    public void No_limit_for_the_run_time_is_written_as_the_TimeSpan_of_Timeout_InfiniteTimeSpan() =>
        Read(("Checkpointing:MaxRuntime", Timeout.InfiniteTimeSpan.ToString("c"))).Checkpointing!.MaxRuntime.ShouldBe(Timeout.InfiniteTimeSpan);

    [Theory]
    [InlineData("Checkpointing:BufferedSteps", "0", "Inngest:Checkpointing:BufferedSteps must be at least 1, but was 0.")]
    [InlineData("Checkpointing:BufferedSteps", "-3", "Inngest:Checkpointing:BufferedSteps must be at least 1, but was -3.")]
    [InlineData("Checkpointing:MaxRuntime", "0s", "Inngest:Checkpointing:MaxRuntime must be greater than zero, or Timeout.InfiniteTimeSpan for no limit, but was 00:00:00.")]
    [InlineData("Checkpointing:MaxRuntime", "-00:00:05", "Inngest:Checkpointing:MaxRuntime must be greater than zero, or Timeout.InfiniteTimeSpan for no limit, but was -00:00:05.")]
    [InlineData("Checkpointing:MaxInterval", "0s", "Inngest:Checkpointing:MaxInterval must be greater than zero, but was 00:00:00.")]
    public void Checkpointing_settings_Inngest_cannot_work_with_are_rejected_by_their_key(string key, string value, string message) =>
        Should.Throw<InvalidOperationException>(() => Read((key, value))).Message.ShouldBe(message);

    [Fact]
    public void Checkpointing_must_be_a_section_rather_than_a_value() =>
        Should.Throw<InvalidOperationException>(() => Read(("Checkpointing", "true"))).Message.ShouldBe(
            "Inngest:Checkpointing must be a section with Enabled, MaxRuntime, BufferedSteps or MaxInterval, but it is 'true'.");

    [Fact]
    public void The_app_id_can_be_read_without_reading_the_other_keys()
    {
        InngestConfiguration.ReadAppId(InngestSection.Of(("AppId", " billing "), ("IsDev", "maybe"))).ShouldBe("billing");
        InngestConfiguration.ReadAppId(InngestSection.Of(("AppId", "  "))).ShouldBeNull();
        InngestConfiguration.ReadAppId(InngestSection.Of(("EventKey", "event-key"))).ShouldBeNull();
    }

    [Fact]
    public void The_section_must_not_be_null() =>
        Should.Throw<ArgumentNullException>(() => InngestConfiguration.Read(null!));
}
