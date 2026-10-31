using Inngest.Blazor;
using Inngest.Realtime;
using Inngest.Tests.Realtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Inngest.Tests.Blazor;

// Each side registers the source it needs, under the one interface a component asks for.
public sealed class RealtimeSourceRegistrationTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task On_the_server_the_source_reads_Inngest_through_the_apps_realtime()
    {
        var api = new FakeRealtimeApi();
        var feed = new SseFeed();
        api.Answer(feed);
        var services = new ServiceCollection().AddLogging();
        services.AddInngest(options =>
        {
            options.AppId = "shop";
            options.IsDev = true;
        });
        services.Replace(ServiceDescriptor.Singleton<IRealtimeApi>(api));
        services.AddInngestRealtimeServerSource();
        await using var provider = services.BuildServiceProvider();

        var source = provider.GetRequiredService<IRealtimeSource>();
        var first = FirstAsync(source.SubscribeAsync(new RealtimeTopic<ServerProgress>("order:7", "status"), Cancel));
        await feed.SendAsync("""{"kind":"data","data":{"text":"Packed","percent":50},"channel":"order:7","topic":"status"}""");

        source.ShouldBeOfType<ServerRealtimeSource>();
        (await first.WaitAsync(Wait, Cancel)).Data.ShouldBe(new ServerProgress("Packed", 50));
        api.TokensMinted.ShouldBe(1);
    }

    [Fact]
    public async Task On_the_server_a_channel_with_topics_gives_the_data_as_JSON()
    {
        var api = new FakeRealtimeApi();
        var feed = new SseFeed();
        api.Answer(feed);
        var services = new ServiceCollection().AddLogging();
        services.AddInngest(options =>
        {
            options.AppId = "shop";
            options.IsDev = true;
        });
        services.Replace(ServiceDescriptor.Singleton<IRealtimeApi>(api));
        services.AddInngestRealtimeServerSource();
        await using var provider = services.BuildServiceProvider();

        var first = FirstAsync(provider.GetRequiredService<IRealtimeSource>().SubscribeAsync("order:7", ["status", "tokens"], Cancel));
        await feed.SendAsync("""{"kind":"data","data":{"text":"Packed"},"channel":"order:7","topic":"status"}""");

        (await first.WaitAsync(Wait, Cancel)).Data.GetProperty("text").GetString().ShouldBe("Packed");
    }

    [Fact]
    public void In_the_browser_the_source_reads_the_relay_and_the_registration_returns_the_builder_of_its_client()
    {
        var services = new ServiceCollection().AddLogging();

        var client = services.AddInngestRealtimeRelaySource(options => options.Url = (channel, _) => new Uri($"/realtime/{channel}", UriKind.Relative));

        client.Name.ShouldBe("Inngest.Realtime");
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IRealtimeSource>().ShouldBeOfType<RelayRealtimeSource>();
    }

    [Fact]
    public void The_registration_made_last_is_the_source_components_get()
    {
        var server = new ServiceCollection().AddLogging();
        server.AddInngest(options =>
        {
            options.AppId = "shop";
            options.IsDev = true;
        });
        server.AddInngestRealtimeRelaySource(options => options.Url = (channel, _) => new Uri($"/realtime/{channel}", UriKind.Relative));
        server.AddInngestRealtimeServerSource();
        var browser = new ServiceCollection().AddLogging();
        browser.AddInngestRealtimeServerSource();
        browser.AddInngestRealtimeRelaySource(options => options.Url = (channel, _) => new Uri($"/realtime/{channel}", UriKind.Relative));

        using var serverProvider = server.BuildServiceProvider();
        using var browserProvider = browser.BuildServiceProvider();
        using var serverScope = serverProvider.CreateScope();
        using var browserScope = browserProvider.CreateScope();

        serverScope.ServiceProvider.GetRequiredService<IRealtimeSource>().ShouldBeOfType<ServerRealtimeSource>();
        browserScope.ServiceProvider.GetRequiredService<IRealtimeSource>().ShouldBeOfType<RelayRealtimeSource>();
        server.Count(descriptor => descriptor.ServiceType == typeof(IRealtimeSource)).ShouldBe(1);
    }

    [Fact]
    public void Registering_twice_leaves_one_source()
    {
        var services = new ServiceCollection();

        services.AddInngestRealtimeServerSource();
        services.AddInngestRealtimeServerSource();

        services.Count(descriptor => descriptor.ServiceType == typeof(IRealtimeSource)).ShouldBe(1);
    }

    [Fact]
    public void Arguments_are_checked()
    {
        Should.Throw<ArgumentNullException>(() => ((IServiceCollection)null!).AddInngestRealtimeServerSource());
        Should.Throw<ArgumentNullException>(() => new ServiceCollection().AddInngestRealtimeRelaySource(null!));
    }

    private static async Task<T> FirstAsync<T>(IAsyncEnumerable<T> source)
    {
        await foreach (var item in source)
        {
            return item;
        }

        throw new InvalidOperationException("The subscription ended.");
    }

    private sealed record ServerProgress(string Text, int Percent);
}
