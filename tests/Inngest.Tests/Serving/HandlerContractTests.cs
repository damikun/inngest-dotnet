using Inngest.Serving;
using Inngest.Tests.Serving.Fakes;

namespace Inngest.Tests.Serving;

// Details of the contract with Inngest that the TypeScript SDK tests per serve adapter and that no other test here
// pins down yet.
public sealed class HandlerContractTests
{
    private const string CallBody = """{"ctx":{"run_id":"01J0000000000000000000000"},"event":{"name":"user/signed.up"},"steps":{}%VERSION%}""";
    private const string InBandBody = """{"url":"https://shop.example.com/api/inngest"}""";

    private readonly CommHandlerHarness _harness = new();

    [Theory]
    [InlineData(",\"version\":1")]
    [InlineData(",\"version\":2")]
    [InlineData(",\"version\":-1")]
    [InlineData("")]
    public async Task Responses_to_calls_are_always_execution_version_2(string version)
    {
        var request = new FakeServeRequest("POST", "http://localhost:3000/api/inngest?fnId=shop-send-email&stepId=step", CallBody.Replace("%VERSION%", version, StringComparison.Ordinal));

        using var response = await _harness.SendAsync(CommHandlerHarness.DevSettings, request);

        response.StatusCode.ShouldBe(200);
        response.Header("X-Inngest-Req-Version").ShouldBe("2");
    }

    [Fact]
    public async Task A_signed_GET_in_cloud_mode_carries_the_full_set_of_sdk_headers_and_nothing_else_from_inngest()
    {
        var request = _harness.Sign(new FakeServeRequest("GET") { Framework = "aspnetcore" }, CommHandlerHarness.PrimaryKey);

        using var response = await _harness.SendAsync(CommHandlerHarness.CloudSettings, request);

        response.StatusCode.ShouldBe(200);
        InngestHeaders(response).ShouldBe(
            ["X-Inngest-Framework", "X-Inngest-Req-Version", "X-Inngest-SDK", "X-Inngest-SDK-Handled", "X-Inngest-Signature"],
            ignoreOrder: true);
        response.Header("X-Inngest-Framework").ShouldBe("aspnetcore");
    }

    [Fact]
    public async Task A_signed_in_band_sync_adds_its_sync_kind_to_that_set()
    {
        var request = _harness.Sign(
            new FakeServeRequest("PUT", body: InBandBody).WithHeader("X-Inngest-Sync-Kind", "in_band"),
            CommHandlerHarness.PrimaryKey);

        using var response = await _harness.SendAsync(CommHandlerHarness.CloudSettings, request);

        response.StatusCode.ShouldBe(200);
        InngestHeaders(response).ShouldBe(
            ["X-Inngest-Framework", "X-Inngest-Req-Version", "X-Inngest-SDK", "X-Inngest-SDK-Handled", "X-Inngest-Signature", "X-Inngest-Sync-Kind"],
            ignoreOrder: true);
        response.Header("X-Inngest-Sync-Kind").ShouldBe("in_band");
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    public async Task A_caller_that_fails_authentication_gets_only_the_marker_that_the_sdk_handled_the_request(string method)
    {
        var settings = CommHandlerHarness.CloudSettings with { AllowUnsignedSync = false };
        var request = new FakeServeRequest(method, "http://localhost:3000/api/inngest?fnId=shop-send-email", "{}");

        using var response = await _harness.SendAsync(settings, request);

        response.StatusCode.ShouldBe(401);
        response.BodyText().ShouldBe("""{"message":"Unauthorized"}""");
        InngestHeaders(response).ShouldBe(["X-Inngest-SDK-Handled"]);
        response.Header("User-Agent").ShouldBeNull();
    }

    [Fact]
    public async Task Refusing_unsigned_syncs_also_refuses_a_signature_that_does_not_verify()
    {
        var settings = CommHandlerHarness.CloudSettings with { AllowUnsignedSync = false };
        var request = new FakeServeRequest("PUT").WithHeader("X-Inngest-Signature", "t=1793275200&s=" + new string('0', 64));

        using var response = await _harness.SendAsync(settings, request);

        response.StatusCode.ShouldBe(401);
        response.BodyText().ShouldBe("""{"message":"Unauthorized"}""");
        _harness.Api.Registrations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Refusing_unsigned_syncs_still_accepts_a_signed_in_band_sync()
    {
        var settings = CommHandlerHarness.CloudSettings with { AllowUnsignedSync = false };
        var request = _harness.Sign(
            new FakeServeRequest("PUT", body: InBandBody).WithHeader("X-Inngest-Sync-Kind", "in_band"),
            CommHandlerHarness.PrimaryKey);

        using var response = await _harness.SendAsync(settings, request);

        response.StatusCode.ShouldBe(200);
        response.Header("X-Inngest-Sync-Kind").ShouldBe("in_band");
    }

    [Fact]
    public async Task A_deploy_id_belongs_to_one_sync_and_is_not_remembered_for_the_next()
    {
        var handler = _harness.CreateHandler(CommHandlerHarness.DevSettings);
        string?[] deployIds = ["1", null, "3"];

        foreach (var deployId in deployIds)
        {
            var url = "http://localhost:3000/api/inngest" + (deployId is null ? string.Empty : "?deployId=" + deployId);
            using var response = await handler.HandleAsync(new FakeServeRequest("PUT", url), TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(200);
        }

        _harness.Api.Registrations.Select(registration => registration.DeployId).ShouldBe(deployIds);
        _harness.Registry.Registrations.Select(registration => registration.ServeUrl).ShouldAllBe(url => url == new Uri("http://localhost:3000/api/inngest"));
    }

    private static string[] InngestHeaders(ServeResponse response) =>
        [.. response.HeaderNames().Where(name => name.StartsWith("X-Inngest-", StringComparison.OrdinalIgnoreCase))];
}
