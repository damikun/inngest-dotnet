using Inngest.Internal;
using Inngest.Tests.Internal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Inngest.Tests.DependencyInjection;

public sealed class AddInngestConfigurationTests
{
    // The configuration is registered as the host does, so that the environment of the machine running the tests
    // stays out of it.
    private static ServiceCollection Services(IConfigurationRoot configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        return services;
    }

    // A host with only the entries given as configuration, as appsettings.json would give them.
    private static HostApplicationBuilder NewHost(params (string Name, string? Value)[] entries)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(entries.Select(entry => new KeyValuePair<string, string?>(entry.Name, entry.Value)));
        return builder;
    }

    [Fact]
    public async Task An_app_id_only_the_section_has_passes_the_startup_check()
    {
        var configuration = InngestSection.Root([("AppId", "billing")]);
        var services = Services(configuration);
        services.AddInngest(configuration.GetSection(InngestSection.Name));
        await using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<InngestOptions>>().Value;

        options.AppId.ShouldBeNull();
        provider.GetRequiredService<InngestSettings>().AppId.ShouldBe("billing");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_app_id_missing_everywhere_still_fails(bool withSection)
    {
        var configuration = InngestSection.Root([("EventKey", "event-key"), ("AppId", " ")]);
        var services = Services(configuration);
        if (withSection)
        {
            services.AddInngest(configuration.GetSection(InngestSection.Name));
        }
        else
        {
            services.AddInngest();
        }

        await using var provider = services.BuildServiceProvider();

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<InngestOptions>>().Value)
            .Message.ShouldContain("InngestOptions.AppId is required");
        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<InngestSettings>())
            .Message.ShouldContain("InngestOptions.AppId is required");
    }

    [Fact]
    public async Task The_app_id_in_code_wins_over_the_section()
    {
        var configuration = InngestSection.Root([("AppId", "billing")]);
        var services = Services(configuration);
        services.AddInngest(configuration.GetSection(InngestSection.Name), options => options.AppId = "payments");
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<InngestSettings>().AppId.ShouldBe("payments");
    }

    [Fact]
    public async Task The_host_starts_with_an_app_id_only_the_section_has()
    {
        var builder = NewHost(("Inngest:AppId", "billing"));
        builder.Services.AddInngest(builder.Configuration.GetSection("Inngest"));
        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_host_does_not_start_without_an_app_id()
    {
        var builder = NewHost(("Inngest:EventKey", "event-key"));
        builder.Services.AddInngest(builder.Configuration.GetSection("Inngest"));
        using var host = builder.Build();

        var exception = await Should.ThrowAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("InngestOptions.AppId is required");
    }

    [Fact]
    public async Task Variables_in_the_host_configuration_win_over_the_section_and_the_callback_wins_over_both()
    {
        var configuration = InngestSection.Root(
            [("AppId", "billing"), ("EventKey", "event-from-file"), ("SigningKey", "signkey-prod-00000001"), ("IsDev", "false")],
            ("INNGEST_EVENT_KEY", "event-from-env"),
            ("INNGEST_DEV", "1"));
        var services = Services(configuration);
        services.AddInngest(configuration.GetSection(InngestSection.Name), options => options.SigningKey = "signkey-prod-00000002");
        await using var provider = services.BuildServiceProvider();

        var settings = provider.GetRequiredService<InngestSettings>();

        settings.AppId.ShouldBe("billing");
        settings.EventKey.ShouldBe("event-from-env");
        settings.IsDev.ShouldBeTrue();
        settings.SigningKey!.Value.ShouldBe("signkey-prod-00000002");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_section_and_Configure_of_the_options_can_be_registered_in_either_order(bool sectionFirst)
    {
        var configuration = InngestSection.Root([("AppId", "billing"), ("EventKey", "event-from-file"), ("Environment", "staging")]);
        var services = Services(configuration);
        void AddSection() => services.AddInngest(configuration.GetSection(InngestSection.Name));
        void ConfigureOptions() => services.Configure<InngestOptions>(options =>
        {
            options.AppVersion = "1.2.3";
            options.EventKey = "event-from-code";
        });

        if (sectionFirst)
        {
            AddSection();
            ConfigureOptions();
        }
        else
        {
            ConfigureOptions();
            AddSection();
        }

        await using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<InngestSettings>();

        settings.AppId.ShouldBe("billing");
        settings.Environment.ShouldBe("staging");
        settings.AppVersion.ShouldBe("1.2.3");
        settings.EventKey.ShouldBe("event-from-code");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddInngest_with_a_section_and_AddInngest_with_a_callback_can_come_in_either_order(bool sectionFirst)
    {
        var configuration = InngestSection.Root([("AppId", "billing"), ("EventKey", "event-from-file"), ("Environment", "staging")]);
        var services = Services(configuration);
        void AddSection() => services.AddInngest(configuration.GetSection(InngestSection.Name));
        void AddCallback() => services.AddInngest(options =>
        {
            options.AppVersion = "1.2.3";
            options.EventKey = "event-from-code";
        });

        if (sectionFirst)
        {
            AddSection();
            AddCallback();
        }
        else
        {
            AddCallback();
            AddSection();
        }

        await using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<InngestSettings>();

        settings.AppId.ShouldBe("billing");
        settings.Environment.ShouldBe("staging");
        settings.AppVersion.ShouldBe("1.2.3");
        settings.EventKey.ShouldBe("event-from-code");
    }

    [Fact]
    public async Task Without_a_section_the_options_hold_no_configuration()
    {
        var services = Services(InngestSection.Root([]));
        services.AddInngest(options => options.AppId = "shop");
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<InngestOptions>>().Value.Configuration.ShouldBeNull();
        provider.GetRequiredService<InngestSettings>().AppId.ShouldBe("shop");
    }

    [Fact]
    public async Task The_client_gets_the_app_id_of_the_section_from_the_builders_services()
    {
        var configuration = InngestSection.Root([("AppId", "billing")]);
        var services = Services(configuration);

        var builder = services.AddInngest(configuration.GetSection(InngestSection.Name));

        builder.Services.ShouldBeSameAs(services);
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IInngestClient>().AppId.ShouldBe("billing");
    }

    [Fact]
    public void The_arguments_must_not_be_null()
    {
        Should.Throw<ArgumentNullException>(() => InngestServiceCollectionExtensions.AddInngest(null!, InngestSection.Of()));
        Should.Throw<ArgumentNullException>(() => new ServiceCollection().AddInngest((IConfiguration)null!));
    }
}
