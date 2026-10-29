using System.Net;
using Inngest.Client;
using Inngest.Signing;
using Microsoft.Extensions.Logging.Testing;

namespace Inngest.Tests.Client;

public sealed class InngestApiTransportTests
{
    private const string Key = "signkey-prod-0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Fallback = "signkey-prod-fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    private readonly FakeHttpHandler _handler = new();
    private readonly FakeLogger _logger = new();

    [Fact]
    public async Task Sends_the_sdk_headers_and_the_hashed_key()
    {
        _handler.Respond(HttpStatusCode.OK, "{}");
        var transport = Create(options => options.Environment = "branch-1");

        using var response = await transport.SendAsync(
            new ApiCall(HttpMethod.Get, transport.Url("/v1/runs/01JRUN")),
            HttpCompletionOption.ResponseContentRead,
            TestContext.Current.CancellationToken);

        var request = _handler.Single;
        request.Uri.ShouldBe(new Uri("https://api.inngest.com/v1/runs/01JRUN"));
        request.Header("Authorization").ShouldBe("Bearer " + SigningKey.Create(Key)!.BearerToken);
        request.Header("X-Inngest-Env").ShouldBe("branch-1");
        request.Header("X-Inngest-SDK").ShouldStartWith("inngest-dotnet:v");
    }

    [Fact]
    public async Task A_call_can_name_another_environment()
    {
        _handler.Respond(HttpStatusCode.OK, "{}");
        var transport = Create(options => options.Environment = "branch-1");

        using var response = await transport.SendAsync(
            new ApiCall(HttpMethod.Post, transport.Url("/v1/signals"), "{}"u8.ToArray(), Environment: "branch-2"),
            HttpCompletionOption.ResponseContentRead,
            TestContext.Current.CancellationToken);

        _handler.Single.Header("X-Inngest-Env").ShouldBe("branch-2");
        _handler.Single.Header("Content-Type").ShouldBe("application/json");
    }

    [Fact]
    public async Task A_rejected_key_is_retried_once_with_the_fallback_key_and_logged_under_the_callers_category()
    {
        _handler.Respond(HttpStatusCode.Unauthorized, """{"error":"bad key"}""").Respond(HttpStatusCode.OK, "{}");
        var transport = Create(options => options.SigningKeyFallback = Fallback);

        using var response = await transport.SendAsync(
            new ApiCall(HttpMethod.Get, transport.Url("/v1/runs/01JRUN")),
            HttpCompletionOption.ResponseContentRead,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _handler.Requests.Count.ShouldBe(2);
        _handler.Requests[1].Header("Authorization").ShouldBe("Bearer " + SigningKey.Create(Fallback)!.BearerToken);
        _logger.Collector.GetSnapshot().ShouldContain(record => record.Id.Id == 312);
    }

    private InngestApiTransport Create(Action<InngestOptions> configure) =>
        new(
            new FakeHttpClientFactory(_handler),
            TestSettings.Cloud(options =>
            {
                options.SigningKey = Key;
                configure(options);
            }),
            _logger);
}
