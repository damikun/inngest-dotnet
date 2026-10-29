using System.Text;
using Inngest.Serving;
using Microsoft.AspNetCore.WebUtilities;

namespace Inngest.Tests.Serving.Fakes;

internal sealed class FakeServeRequest(string method, string url = "http://localhost:3000/api/inngest", string? body = null) : IServeRequest
{
    public string Method { get; } = method;

    public Uri Url { get; } = new(url);

    public ReadOnlyMemory<byte> Body { get; init; } = body is null ? ReadOnlyMemory<byte>.Empty : Encoding.UTF8.GetBytes(body);

    public string Framework { get; init; } = "test";

    public IServiceProvider? Services { get; init; }

    public Dictionary<string, string> RequestHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? GetHeader(string name) => RequestHeaders.GetValueOrDefault(name);

    // Case-insensitive and decoded, like HttpRequest.Query.
    public string? GetQuery(string name) =>
        QueryHelpers.ParseQuery(Url.Query).TryGetValue(name, out var value) ? value.ToString() : null;

    public FakeServeRequest WithHeader(string name, string value)
    {
        RequestHeaders[name] = value;
        return this;
    }
}
