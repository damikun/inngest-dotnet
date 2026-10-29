namespace Inngest.Tests.Client;

internal sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    private readonly List<string> _names = [];

    public IReadOnlyList<string> RequestedNames => _names;

    public HttpClient CreateClient(string name)
    {
        _names.Add(name);
        return new HttpClient(handler, disposeHandler: false);
    }
}
