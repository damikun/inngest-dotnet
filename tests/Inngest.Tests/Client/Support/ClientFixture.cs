using Inngest.Client;
using Inngest.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Inngest.Tests.Client;

// An InngestClient wired to a fake HTTP handler and a fake clock.
internal sealed class ClientFixture
{
    public ClientFixture(InngestSettings? settings = null)
    {
        Settings = settings ?? TestSettings.Create();
        Factory = new FakeHttpClientFactory(Handler);

        var provider = new ServiceCollection().BuildServiceProvider();
        Client = new InngestClient(
            Factory,
            Settings,
            Time,
            provider.GetRequiredService<IServiceScopeFactory>(),
            Logger,
            provider.GetRequiredService<IServiceProviderIsService>());
    }

    public FakeHttpHandler Handler { get; } = new();

    public FakeHttpClientFactory Factory { get; }

    // The example time from the protocol notes: 2024-01-18T14:01:44Z.
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2024, 1, 18, 14, 1, 44, TimeSpan.Zero));

    public FakeLogger<InngestClient> Logger { get; } = new();

    public InngestSettings Settings { get; }

    public InngestClient Client { get; }

    public const string Accepted = """{"ids":["01HQ0000000000000000000001"],"status":200}""";
}
