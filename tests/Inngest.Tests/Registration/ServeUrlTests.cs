using Inngest.Registration;
using Microsoft.AspNetCore.WebUtilities;
using static Inngest.Tests.Registration.RegistryFixtures;

namespace Inngest.Tests.Registration;

public sealed class ServeUrlTests
{
    private const string Step = "fnId=my-app-orders&stepId=step";

    [Theory]
    [InlineData("https://example.com/api/inngest")]
    [InlineData("http://localhost:3000/api/inngest")]
    [InlineData("https://example.com:8443/inngest")]
    public void Adds_the_function_and_step_to_a_plain_url(string serve)
    {
        var url = new ServeUrl(new Uri(serve));

        url.App.ShouldBe(serve);
        url.Step("my-app-orders").ShouldBe($"{serve}?{Step}");
    }

    [Fact]
    public void Keeps_a_trailing_slash()
    {
        var url = new ServeUrl(new Uri("https://example.com/api/inngest/"));

        url.App.ShouldBe("https://example.com/api/inngest/");
        url.Step("my-app-orders").ShouldBe($"https://example.com/api/inngest/?{Step}");
    }

    [Fact]
    public void A_bare_origin_gets_the_root_path()
    {
        var url = new ServeUrl(new Uri("https://example.com"));

        url.App.ShouldBe("https://example.com/");
        url.Step("my-app-orders").ShouldBe($"https://example.com/?{Step}");
    }

    [Fact]
    public void Drops_the_default_port_and_lowercases_the_host_as_uri_does()
    {
        var url = new ServeUrl(new Uri("https://EXAMPLE.com:443/Api/Inngest"));

        url.App.ShouldBe("https://example.com/Api/Inngest");
    }

    [Fact]
    public void Websocket_urls_of_connect_apps_work_the_same_way()
    {
        var url = new ServeUrl(new Uri("wss://connect"));

        url.App.ShouldBe("wss://connect/");
        url.Step("my-app-orders").ShouldBe($"wss://connect/?{Step}");
    }

    [Fact]
    public void Keeps_existing_query_parameters_in_order_and_puts_the_function_after_them()
    {
        var url = new ServeUrl(new Uri("https://example.com/api/inngest?region=eu&tenant=7"));

        url.App.ShouldBe("https://example.com/api/inngest?region=eu&tenant=7");
        url.Step("my-app-orders").ShouldBe($"https://example.com/api/inngest?region=eu&tenant=7&{Step}");
    }

    [Theory]
    [InlineData("?deployId=abc123", "", "")]
    [InlineData("?deployId=abc123&region=eu", "?region=eu", "region=eu&")]
    [InlineData("?region=eu&deployId=abc123", "?region=eu", "region=eu&")]
    [InlineData("?a=1&deployId=abc123&b=2", "?a=1&b=2", "a=1&b=2&")]
    [InlineData("?deployId=one&deployId=two", "", "")]
    [InlineData("?deployId=", "", "")]
    [InlineData("?deployId", "", "")]
    public void Strips_deploy_id_from_both_urls(string query, string expectedAppQuery, string expectedStepQueryPrefix)
    {
        var url = new ServeUrl(new Uri($"https://example.com/api/inngest{query}"));

        url.App.ShouldBe($"https://example.com/api/inngest{expectedAppQuery}");
        url.Step("my-app-orders").ShouldBe($"https://example.com/api/inngest?{expectedStepQueryPrefix}{Step}");
    }

    [Theory]
    [InlineData("deployIds=1")]
    [InlineData("xdeployId=1")]
    [InlineData("DEPLOYID=1")]
    [InlineData("deploy_id=1")]
    public void Only_the_exact_deploy_id_parameter_is_stripped(string parameter)
    {
        var url = new ServeUrl(new Uri($"https://example.com/api/inngest?{parameter}"));

        url.App.ShouldBe($"https://example.com/api/inngest?{parameter}");
    }

    [Fact]
    public void Replaces_function_and_step_parameters_that_the_url_already_carries()
    {
        var url = new ServeUrl(new Uri("https://example.com/api/inngest?fnId=other&region=eu&stepId=abc"));

        url.Step("my-app-orders").ShouldBe($"https://example.com/api/inngest?region=eu&{Step}");
        url.App.ShouldBe("https://example.com/api/inngest?fnId=other&region=eu&stepId=abc");
    }

    [Fact]
    public void Ignores_empty_query_pairs()
    {
        var url = new ServeUrl(new Uri("https://example.com/api/inngest?&a=1&&b=2&"));

        url.App.ShouldBe("https://example.com/api/inngest?a=1&b=2");
    }

    [Fact]
    public void A_lone_question_mark_leaves_no_query()
    {
        var url = new ServeUrl(new Uri("https://example.com/api/inngest?"));

        url.App.ShouldBe("https://example.com/api/inngest");
        url.Step("my-app-orders").ShouldBe($"https://example.com/api/inngest?{Step}");
    }

    [Fact]
    public void Drops_the_fragment()
    {
        var url = new ServeUrl(new Uri("https://example.com/api/inngest?region=eu#section"));

        url.App.ShouldBe("https://example.com/api/inngest?region=eu");
        url.Step("my-app-orders").ShouldBe($"https://example.com/api/inngest?region=eu&{Step}");
    }

    [Theory]
    [InlineData("?q=a%20b", "q=a%20b")]
    [InlineData("?q=a b", "q=a%20b")]
    [InlineData("?q=a+b", "q=a+b")]
    [InlineData("?q=%2B&r=%26", "q=%2B&r=%26")]
    [InlineData("?q=%C3%A9", "q=%C3%A9")]
    [InlineData("?q=é", "q=%C3%A9")]
    [InlineData("?q=a=b", "q=a=b")]
    public void Keeps_the_encoding_of_existing_parameters(string query, string expected)
    {
        var url = new ServeUrl(new Uri($"https://example.com/api/inngest{query}"));

        url.App.ShouldBe($"https://example.com/api/inngest?{expected}");
        url.Step("my-app-orders").ShouldBe($"https://example.com/api/inngest?{expected}&{Step}");
    }

    [Theory]
    [InlineData("https://example.com/api/a%20b", "https://example.com/api/a%20b")]
    [InlineData("https://example.com/api/a b", "https://example.com/api/a%20b")]
    [InlineData("https://example.com/api/ü", "https://example.com/api/%C3%BC")]
    public void Encodes_the_path_as_uri_does(string serve, string expected) =>
        new ServeUrl(new Uri(serve)).App.ShouldBe(expected);

    [Theory]
    [InlineData("my-app-orders", "my-app-orders")]
    [InlineData("my-app-a.b_c~d", "my-app-a.b_c~d")]
    [InlineData("my-app-Send Email", "my-app-Send%20Email")]
    [InlineData("my-app-orders/refund", "my-app-orders%2Frefund")]
    [InlineData("my-app-a&b=c", "my-app-a%26b%3Dc")]
    [InlineData("my-app-a+b", "my-app-a%2Bb")]
    [InlineData("my-app-100%", "my-app-100%25")]
    [InlineData("my-app-Zürich", "my-app-Z%C3%BCrich")]
    [InlineData("my-app-it's", "my-app-it%27s")]
    [InlineData("my-app-a#b?c", "my-app-a%23b%3Fc")]
    public void Encodes_the_function_id_so_that_it_stays_one_parameter(string compositeId, string expected)
    {
        var url = new ServeUrl(new Uri(DefaultServeUrl));

        url.Step(compositeId).ShouldBe($"{DefaultServeUrl}?fnId={expected}&stepId=step");
    }

    [Theory]
    [InlineData("my-app-orders")]
    [InlineData("my-app-Send Email")]
    [InlineData("my-app-a&b=c+d%e#f?g")]
    [InlineData("my-app-Zürich/それ")]
    public void The_function_id_reads_back_exactly_as_the_serve_endpoint_parses_it(string compositeId)
    {
        var step = new ServeUrl(new Uri($"{DefaultServeUrl}?region=eu&deployId=1")).Step(compositeId);

        var query = QueryHelpers.ParseQuery(new Uri(step).Query);

        query["fnId"].ToString().ShouldBe(compositeId);
        query["stepId"].ToString().ShouldBe("step");
        query["region"].ToString().ShouldBe("eu");
        query.ContainsKey("deployId").ShouldBeFalse();
    }

    [Fact]
    public void Rejects_a_relative_url() =>
        Should.Throw<ArgumentException>(() => new ServeUrl(new Uri("/api/inngest", UriKind.Relative)));
}
