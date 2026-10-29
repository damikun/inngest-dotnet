using Inngest.Serving;

namespace Inngest.Tests.Serving;

public sealed class ServeUrlTests
{
    [Theory]
    [InlineData("http://localhost:3000/api/inngest?", "http://localhost:3000/api/inngest")]
    [InlineData("http://localhost:3000/api/inngest?deployId=1&&region=eu", "http://localhost:3000/api/inngest?region=eu")]
    [InlineData("http://localhost:3000/api/inngest?deployId", "http://localhost:3000/api/inngest")]
    [InlineData("http://localhost:3000/api/inngest?a=1&&b=2", "http://localhost:3000/api/inngest?a=1&&b=2")]
    [InlineData("http://localhost:3000/api/inngest?name=a%20b&deployId=1", "http://localhost:3000/api/inngest?name=a%20b")]
    [InlineData("http://localhost:3000/api/inngest#fragment", "http://localhost:3000/api/inngest")]
    [InlineData("http://[::1]:8080/api/inngest", "http://[::1]:8080/api/inngest")]
    public void Only_deploy_ids_and_fragments_are_removed(string url, string expected)
    {
        ServeUrl.Resolve(new Uri(url), serveOrigin: null, servePath: null).ShouldBe(new Uri(expected));
    }

    [Theory]
    [InlineData("https://shop.example.com", "https://shop.example.com/api/inngest")]
    [InlineData("https://shop.example.com/", "https://shop.example.com/api/inngest")]
    [InlineData("https://shop.example.com:443", "https://shop.example.com/api/inngest")]
    [InlineData("http://shop.example.com:8080/base/", "http://shop.example.com:8080/api/inngest")]
    public void The_serve_origin_replaces_scheme_host_and_port(string serveOrigin, string expected)
    {
        ServeUrl.Resolve(new Uri("http://localhost:3000/api/inngest"), new Uri(serveOrigin), servePath: null).ShouldBe(new Uri(expected));
    }

    [Theory]
    [InlineData("/", "http://localhost:3000/")]
    [InlineData("/my path/inngest", "http://localhost:3000/my%20path/inngest")]
    [InlineData("functions/inngest", "http://localhost:3000/functions/inngest")]
    public void The_serve_path_replaces_the_path(string servePath, string expected)
    {
        ServeUrl.Resolve(new Uri("http://localhost:3000/api/inngest"), serveOrigin: null, servePath).AbsoluteUri.ShouldBe(expected);
    }
}
