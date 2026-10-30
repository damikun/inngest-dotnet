using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Inngest.EntityFrameworkCore.Tests;

// The outbox in a generic host, the way an app runs it: the host starts the dispatcher, checks the options, and stops it.
public sealed class HostTests
{
    [Fact(Timeout = 60_000)]
    public async Task A_host_runs_the_dispatcher_from_start_to_stop()
    {
        using var database = SqliteDatabase.Shared();
        var client = new RecordingInngestClient();
        using var host = Build(database, client, options => options.PollInterval = TimeSpan.FromHours(1));
        var cancellation = TestContext.Current.CancellationToken;

        using (var scope = host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ShopContext>().Database.EnsureCreatedAsync(cancellation);
        }

        await host.StartAsync(cancellation);
        using (var scope = host.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ShopContext>();
            context.Orders.Add(new Order { Number = "A-1" });
            context.AddInngestEvent(new OrderPlaced("A-1", 10));
            await context.SaveChangesAsync(cancellation);
        }

        // The poll is an hour away, so the save woke the dispatcher.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (client.Events.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, cancellation);
        }

        client.Events.ShouldHaveSingleItem().Name.ShouldBe("shop/order.placed");
        await host.StopAsync(cancellation);
    }

    [Fact]
    public async Task A_host_with_options_that_cannot_work_does_not_start()
    {
        using var database = SqliteDatabase.Shared();
        using var host = Build(database, new RecordingInngestClient(), options => options.BatchSize = 0);

        var failure = await Should.ThrowAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        failure.Message.ShouldContain("BatchSize");
    }

    private static IHost Build(SqliteDatabase database, RecordingInngestClient client, Action<InngestOutboxOptions> configure)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddSingleton<IInngestClient>(client);
        builder.Services.AddInngest(options =>
        {
            options.AppId = "shop";
            options.IsDev = true;
        }).AddEntityFrameworkOutbox<ShopContext>(configure);
        builder.Services.AddDbContext<ShopContext>((services, options) =>
        {
            database.Configure(options);
            options.UseInngestOutbox(services);
        });
        return builder.Build();
    }
}
