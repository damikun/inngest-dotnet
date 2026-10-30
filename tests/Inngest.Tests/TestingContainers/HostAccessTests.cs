using Inngest.Testing.Containers;

namespace Inngest.Tests.TestingContainers;

public sealed class HostAccessTests
{
    [Theory]
    [InlineData("http://localhost:5000/api/inngest", "http://host.docker.internal:5000/api/inngest")]
    [InlineData("http://LOCALHOST:5000/api/inngest", "http://host.docker.internal:5000/api/inngest")]
    [InlineData("http://127.0.0.1:5000", "http://host.docker.internal:5000/")]
    [InlineData("http://127.1.2.3:5000/x", "http://host.docker.internal:5000/x")]
    [InlineData("http://[::1]:5000/x", "http://host.docker.internal:5000/x")]
    [InlineData("http://0.0.0.0:5000/x", "http://host.docker.internal:5000/x")]
    [InlineData("http://[::]:5000/x", "http://host.docker.internal:5000/x")]
    [InlineData("http://localhost/x", "http://host.docker.internal/x")]
    [InlineData("https://localhost:5001/x?a=1&b=2", "https://host.docker.internal:5001/x?a=1&b=2")]
    [InlineData("http://shop:5000/x", "http://shop:5000/x")]
    [InlineData("http://10.0.0.5:5000/x", "http://10.0.0.5:5000/x")]
    [InlineData("http://host.docker.internal:5000/x", "http://host.docker.internal:5000/x")]
    public void A_url_that_means_the_host_becomes_the_name_the_container_reaches_it_by(string url, string expected)
    {
        HostAccess.ForContainer(new Uri(url)).AbsoluteUri.ShouldBe(expected);
    }

    [Theory]
    [InlineData("http://[::]:5000/api/inngest", "http://localhost:5000/api/inngest")]
    [InlineData("http://0.0.0.0:5000/api/inngest", "http://localhost:5000/api/inngest")]
    [InlineData("http://127.0.0.1:5000/api/inngest", "http://127.0.0.1:5000/api/inngest")]
    [InlineData("http://shop:5000/api/inngest", "http://shop:5000/api/inngest")]
    public void The_test_connects_to_localhost_where_kestrel_names_all_interfaces(string url, string expected)
    {
        HostAccess.ForTest(new Uri(url)).AbsoluteUri.ShouldBe(expected);
    }

    [Fact]
    public void A_relative_url_is_rejected()
    {
        Should.Throw<ArgumentException>(() => HostAccess.ForContainer(new Uri("/api/inngest", UriKind.Relative)));
    }

    [Fact]
    public void Only_http_and_https_urls_are_apps()
    {
        Should.Throw<ArgumentException>(() => HostAccess.ForContainer(new Uri("ws://localhost:5000/x")));
    }
}
