using Inngest.Client;
using Inngest.Internal;
using Microsoft.Extensions.Logging.Testing;

namespace Inngest.Tests.Client;

// An InngestApiClient wired to a fake HTTP handler.
internal sealed class ApiFixture
{
    public ApiFixture(InngestSettings? settings = null)
    {
        Factory = new FakeHttpClientFactory(Handler);
        Api = new InngestApiClient(Factory, settings ?? TestSettings.Create(), Logger);
    }

    public FakeHttpHandler Handler { get; } = new();

    public FakeHttpClientFactory Factory { get; }

    public FakeLogger<InngestApiClient> Logger { get; } = new();

    public InngestApiClient Api { get; }
}
