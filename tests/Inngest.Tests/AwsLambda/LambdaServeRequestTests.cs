using System.Text;
using Amazon.Lambda.APIGatewayEvents;
using Inngest.AwsLambda;

namespace Inngest.Tests.AwsLambda;

// The corners of events that real API Gateways rarely produce but test events, emulators and other tools do.
public sealed class LambdaServeRequestTests
{
    [Fact]
    public void Headers_of_a_rest_event_are_merged_from_both_forms_and_matched_without_regard_to_case()
    {
        var request = LambdaServeRequest.Create(
            new APIGatewayProxyRequest
            {
                HttpMethod = "POST",
                Headers = new Dictionary<string, string> { ["X-Single"] = "only", ["x-both"] = "single", [" Padded "] = "p" },
                MultiValueHeaders = new Dictionary<string, IList<string>> { ["X-BOTH"] = ["multi1", "multi2"], ["X-Multi"] = ["a", "b"], ["X-Empty"] = [] },
            },
            default);

        request.GetHeader("x-single").ShouldBe("only");
        request.GetHeader("X-Both").ShouldBe("multi1,multi2");
        request.GetHeader("x-multi").ShouldBe("a,b");
        request.GetHeader("padded").ShouldBe("p");
        request.GetHeader("x-empty").ShouldBeNull();
        request.GetHeader("x-missing").ShouldBeNull();
    }

    [Fact]
    public void An_event_may_leave_out_every_collection()
    {
        var request = LambdaServeRequest.Create(new APIGatewayProxyRequest { HttpMethod = "GET" }, default);

        request.Method.ShouldBe("GET");
        request.GetHeader("host").ShouldBeNull();
        request.GetQuery("fnId").ShouldBeNull();
        request.Body.IsEmpty.ShouldBeTrue();
        request.Services.ShouldBeNull();
        request.Framework.ShouldBe("aws-lambda");
    }

    [Fact]
    public void A_header_without_a_value_is_left_out()
    {
        var request = LambdaServeRequest.Create(
            new APIGatewayHttpApiV2ProxyRequest
            {
                RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext { Http = new APIGatewayHttpApiV2ProxyRequest.HttpDescription { Method = "GET" } },
                Headers = new Dictionary<string, string> { ["x-null"] = null!, ["x-set"] = "1" },
            },
            default);

        request.GetHeader("x-null").ShouldBeNull();
        request.GetHeader("x-set").ShouldBe("1");
    }

    [Fact]
    public void The_method_of_a_rest_event_may_come_from_its_request_context()
    {
        var request = LambdaServeRequest.Create(
            new APIGatewayProxyRequest { RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { HttpMethod = "PUT" } },
            default);

        request.Method.ShouldBe("PUT");
    }

    [Fact]
    public void The_body_is_the_one_the_caller_passes()
    {
        var body = Encoding.UTF8.GetBytes("{}");

        var request = LambdaServeRequest.Create(new APIGatewayProxyRequest { HttpMethod = "POST" }, body);

        request.Body.ToArray().ShouldBe(body);
    }

    [Fact]
    public void A_raw_query_string_with_a_leading_question_mark_is_read_as_one_without()
    {
        var request = LambdaServeRequest.Create(
            new APIGatewayHttpApiV2ProxyRequest
            {
                RawQueryString = "?fnId=shop-send-email&stepId=step",
                RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext { Http = new APIGatewayHttpApiV2ProxyRequest.HttpDescription { Method = "POST" } },
            },
            default);

        request.GetQuery("fnId").ShouldBe("shop-send-email");
        request.GetQuery("stepId").ShouldBe("step");
    }

    [Fact]
    public void Url_is_not_built_until_something_asks_for_it()
    {
        // No host anywhere: a call never needs the URL, so only a sync fails.
        var request = LambdaServeRequest.Create(new APIGatewayProxyRequest { HttpMethod = "POST", Path = "/api/inngest" }, default);

        request.GetQuery("anything").ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => request.Url).Message.ShouldContain("ServeOrigin");
    }

    [Fact]
    public void A_host_that_makes_no_url_is_explained()
    {
        var request = LambdaServeRequest.Create(
            new APIGatewayProxyRequest { HttpMethod = "PUT", Path = "/api/inngest", Headers = new Dictionary<string, string> { ["Host"] = "bad host name" } },
            default);

        var exception = Should.Throw<InvalidOperationException>(() => request.Url);

        exception.Message.ShouldContain("bad host name");
        exception.Message.ShouldContain("ServePath");
    }

    [Fact]
    public void A_path_without_a_leading_slash_gets_one()
    {
        var request = LambdaServeRequest.Create(
            new APIGatewayProxyRequest { HttpMethod = "PUT", Path = "api/inngest", Headers = new Dictionary<string, string> { ["Host"] = "example.com" } },
            default);

        request.Url.ShouldBe(new Uri("https://example.com/api/inngest"));
    }
}
