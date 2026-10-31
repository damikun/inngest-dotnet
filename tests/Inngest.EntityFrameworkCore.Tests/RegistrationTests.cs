using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Inngest.EntityFrameworkCore.Tests;

// The setup an app writes: the model, the registration, the options, and what is said when one of them is missing.
public sealed class RegistrationTests
{
    [Fact]
    public void The_defaults_are_valid()
    {
        using var provider = BuildServices(configure: null);

        Should.NotThrow(() => provider.GetRequiredService<IStartupValidator>().Validate());
    }

    [Fact]
    public void Options_that_cannot_work_stop_the_app_at_startup_and_every_problem_is_named()
    {
        using var provider = BuildServices(options =>
        {
            options.PollInterval = TimeSpan.Zero;
            options.BatchSize = 0;
            options.SendTimeout = TimeSpan.FromMinutes(2);
            options.LeaseDuration = TimeSpan.FromMinutes(1);
            options.RetryDelay = TimeSpan.FromMinutes(10);
            options.MaxRetryDelay = TimeSpan.FromMinutes(1);
            options.Retention = TimeSpan.FromSeconds(-1);
            options.CleanupInterval = TimeSpan.Zero;
            options.CleanupBatchSize = 0;
        });

        var failure = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        failure.Failures.Count().ShouldBe(7);
        var message = failure.Message;
        message.ShouldContain("ShopContext");
        message.ShouldContain("PollInterval");
        message.ShouldContain("BatchSize");
        message.ShouldContain("LeaseDuration must be at least twice SendTimeout");
        message.ShouldContain("MaxRetryDelay must not be shorter than RetryDelay");
        message.ShouldContain("Retention");
        message.ShouldContain("CleanupInterval");
        message.ShouldContain("CleanupBatchSize");
    }

    [Fact]
    public void A_lease_that_barely_covers_one_request_is_refused_because_a_refused_request_could_not_be_followed_by_another()
    {
        // The dispatcher would claim events, find the lease too short for the single requests that follow a refusal, and give them back
        // for the next pass to claim again.
        using var provider = BuildServices(options =>
        {
            options.SendTimeout = TimeSpan.FromSeconds(2);
            options.LeaseDuration = TimeSpan.FromSeconds(2.2);
        });

        var failure = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        failure.Failures.ShouldHaveSingleItem().ShouldContain("LeaseDuration must be at least twice SendTimeout");

        using var enough = BuildServices(options =>
        {
            options.SendTimeout = TimeSpan.FromSeconds(2);
            options.LeaseDuration = TimeSpan.FromSeconds(4);
        });
        Should.NotThrow(() => enough.GetRequiredService<IStartupValidator>().Validate());
    }

    [Fact]
    public void A_context_that_maps_the_outbox_has_its_table_with_the_columns_the_dispatcher_uses()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        using var context = new ShopContext(new DbContextOptionsBuilder<ShopContext>().UseSqlite(connection).Options);

        var entity = context.Model.FindEntityType(typeof(InngestOutboxMessage)).ShouldNotBeNull();

        entity.GetTableName().ShouldBe("InngestOutbox");
        entity.FindPrimaryKey()!.Properties.Select(property => property.Name).ShouldBe(["Id"]);
        entity.FindProperty(nameof(InngestOutboxMessage.Id))!.GetMaxLength().ShouldBe(255);
        entity.FindProperty(nameof(InngestOutboxMessage.Id))!.ValueGenerated.ShouldBe(Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never);
        entity.FindProperty(nameof(InngestOutboxMessage.Data))!.IsNullable.ShouldBeFalse();
        entity.FindProperty(nameof(InngestOutboxMessage.CreatedAt))!.IsNullable.ShouldBeFalse();
        entity.FindProperty(nameof(InngestOutboxMessage.Version))!.GetMaxLength().ShouldBe(255);
        entity.FindProperty(nameof(InngestOutboxMessage.Name))!.GetMaxLength().ShouldBe(255);
        entity.FindProperty(nameof(InngestOutboxMessage.User))!.IsNullable.ShouldBeTrue();
        entity.GetIndexes().Single().Properties.Select(property => property.Name).ShouldBe(["SentAt", "NextAttemptAt"]);
    }

    [Fact]
    public void The_mapping_can_be_changed_by_the_app()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        using var context = new RenamedContext(new DbContextOptionsBuilder<RenamedContext>().UseSqlite(connection).Options);

        var entity = context.Model.FindEntityType(typeof(InngestOutboxMessage)).ShouldNotBeNull();

        entity.GetTableName().ShouldBe("Outbox");
        entity.GetSchema().ShouldBe("inngest");
    }

    [Fact]
    public async Task Adding_an_event_to_a_context_that_does_not_map_the_outbox_says_what_to_call()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        var services = new ServiceCollection();
        services.AddInngest(options =>
        {
            options.AppId = "shop";
            options.IsDev = true;
        }).AddEntityFrameworkOutbox<PlainContext>();
        services.AddDbContext<PlainContext>(options => options.UseSqlite(connection));
        await using var provider = services.BuildServiceProvider();
        await using var context = provider.CreateScope().ServiceProvider.GetRequiredService<PlainContext>();

        var failure = Should.Throw<InvalidOperationException>(() => context.AddInngestEvent(new OrderPlaced("A-1", 10)));

        failure.Message.ShouldContain("PlainContext");
        failure.Message.ShouldContain("modelBuilder.AddInngestOutbox()");
    }

    [Fact]
    public async Task Adding_an_event_before_the_outbox_is_registered_says_what_to_call()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        var services = new ServiceCollection();
        services.AddInngest(options =>
        {
            options.AppId = "shop";
            options.IsDev = true;
        });
        services.AddDbContext<ShopContext>(options => options.UseSqlite(connection));
        await using var provider = services.BuildServiceProvider();
        await using var context = provider.CreateScope().ServiceProvider.GetRequiredService<ShopContext>();

        var failure = Should.Throw<InvalidOperationException>(() => context.AddInngestEvent(new OrderPlaced("A-1", 10)));

        failure.Message.ShouldContain("AddEntityFrameworkOutbox<ShopContext>()");
    }

    [Fact]
    public void A_context_made_by_hand_does_not_know_the_services_of_the_app()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        using var context = new ShopContext(new DbContextOptionsBuilder<ShopContext>().UseSqlite(connection).Options);

        var failure = Should.Throw<InvalidOperationException>(() => context.AddInngestEvent(new OrderPlaced("A-1", 10)));

        failure.Message.ShouldContain("AddDbContext");
    }

    [Fact]
    public void Waking_the_dispatcher_needs_the_outbox_to_be_registered_first()
    {
        var services = new ServiceCollection();
        services.AddInngest(options =>
        {
            options.AppId = "shop";
            options.IsDev = true;
        });
        using var provider = services.BuildServiceProvider();

        var failure = Should.Throw<InvalidOperationException>(() => new DbContextOptionsBuilder<ShopContext>().UseInngestOutbox(provider));

        failure.Message.ShouldContain("AddEntityFrameworkOutbox<TContext>()");
    }

    [Fact]
    public async Task Registering_a_second_context_gives_it_a_dispatcher_and_settings_of_its_own()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInngest(options =>
        {
            options.AppId = "shop";
            options.IsDev = true;
        })
            .AddEntityFrameworkOutbox<ShopContext>(options => options.BatchSize = 10)
            .AddEntityFrameworkOutbox<PlainContext>(options => options.BatchSize = 20);
        await using var provider = services.BuildServiceProvider();

        provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().Count(service => service.GetType().Name.StartsWith("OutboxDispatcher", StringComparison.Ordinal)).ShouldBe(2);
        provider.GetRequiredService<OutboxProcessor<ShopContext>>().Settings.BatchSize.ShouldBe(10);
        provider.GetRequiredService<OutboxProcessor<PlainContext>>().Settings.BatchSize.ShouldBe(20);
    }

    private static ServiceProvider BuildServices(Action<InngestOutboxOptions>? configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInngest(options =>
        {
            options.AppId = "shop";
            options.IsDev = true;
        }).AddEntityFrameworkOutbox<ShopContext>(configure);
        return services.BuildServiceProvider();
    }
}
