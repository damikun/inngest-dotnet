using System.Security.Claims;
using System.Text;
using Microsoft.Azure.Functions.Worker.Http;

namespace Inngest.Tests.AzureFunctions.Fakes;

// A request as the built-in HTTP model hands it over. Like the worker's own, the body is a MemoryStream that holds the
// whole of it, and the host has put every header name in lower case.
internal sealed class FakeHttpRequestData : HttpRequestData
{
    private readonly HttpHeadersCollection _headers = new();

    public FakeHttpRequestData(FakeFunctionContext context, string method, string url, Stream? body = null)
        : base(context)
    {
        Method = method;
        Url = new Uri(url);
        Body = body ?? new MemoryStream();
    }

    public override Stream Body { get; }

    public override HttpHeadersCollection Headers => _headers;

    public override IReadOnlyCollection<IHttpCookie> Cookies => [];

    public override Uri Url { get; }

    public override IEnumerable<ClaimsIdentity> Identities => [];

    public override string Method { get; }

    // The response created last, which a function returns.
    public FakeHttpResponseData? Response { get; private set; }

    public static MemoryStream BodyOf(byte[] bytes)
    {
        var stream = new MemoryStream(bytes.Length);
        stream.Write(bytes);
        stream.Position = 0;
        return stream;
    }

    public static MemoryStream BodyOf(string text) => BodyOf(Encoding.UTF8.GetBytes(text));

    public FakeHttpRequestData WithHeader(string name, string value)
    {
        _headers.TryAddWithoutValidation(name.ToLowerInvariant(), value);
        return this;
    }

    public override HttpResponseData CreateResponse() => Response = new FakeHttpResponseData(FunctionContext);
}
