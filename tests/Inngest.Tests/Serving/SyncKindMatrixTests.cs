using Inngest.Tests.Serving.Fakes;

namespace Inngest.Tests.Serving;

// Which kind of sync a PUT gets, for every combination of the mode of the app, what the server asked for, how the
// request was signed and whether in-band syncs are allowed. The TypeScript SDK runs the same matrix for each serve
// adapter, with the kind of server as one more dimension.
public sealed class SyncKindMatrixTests
{
    private const string Body = """{"url":"https://example.com/api/inngest"}""";

    private readonly CommHandlerHarness _harness = new();

    public static TheoryData<string, bool, string?, string, bool, string> Cases()
    {
        var cases = new TheoryData<string, bool, string?, string, bool, string>();
        foreach (var serverKind in new[] { "dev", "cloud" })
        {
            foreach (var cloudApp in new[] { false, true })
            {
                foreach (var requested in new[] { null, "in_band", "out_of_band" })
                {
                    foreach (var signature in new[] { "none", "invalid", "valid" })
                    {
                        foreach (var allowInBand in new[] { true, false })
                        {
                            cases.Add(serverKind, cloudApp, requested, signature, allowInBand, Expected(cloudApp, requested, signature, allowInBand));
                        }
                    }
                }
            }
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task The_kind_of_sync_follows_the_request_the_signature_and_the_setting(
        string serverKind,
        bool cloudApp,
        string? requested,
        string signature,
        bool allowInBand,
        string expected)
    {
        var settings = (cloudApp ? CommHandlerHarness.CloudSettings : CommHandlerHarness.DevSettings) with { AllowInBandSync = allowInBand };
        var request = new FakeServeRequest("PUT", body: Body).WithHeader("X-Inngest-Server-Kind", serverKind);
        if (requested is not null)
        {
            request.WithHeader("X-Inngest-Sync-Kind", requested);
        }

        request = signature switch
        {
            "valid" => _harness.Sign(request, CommHandlerHarness.PrimaryKey),
            "invalid" => request.WithHeader("X-Inngest-Signature", "INVALID"),
            _ => request,
        };

        using var response = await _harness.SendAsync(settings, request);

        if (expected == "401")
        {
            response.StatusCode.ShouldBe(401);
            return;
        }

        response.StatusCode.ShouldBe(200);

        // An app in cloud mode tells a caller it could not verify nothing about itself, not even the kind of sync.
        var identifies = !cloudApp || signature == "valid";
        response.Header("X-Inngest-Sync-Kind").ShouldBe(identifies ? expected : null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_in_band_sync_without_a_body_is_a_bad_request_not_a_sync(bool cloudApp)
    {
        var settings = cloudApp ? CommHandlerHarness.CloudSettings : CommHandlerHarness.DevSettings;
        var request = new FakeServeRequest("PUT").WithHeader("X-Inngest-Sync-Kind", "in_band");
        if (cloudApp)
        {
            request = _harness.Sign(request, CommHandlerHarness.PrimaryKey);
        }

        using var response = await _harness.SendAsync(settings, request);

        response.StatusCode.ShouldBe(400);
        _harness.Registry.Registrations.ShouldBeEmpty();
        _harness.Api.Registrations.ShouldBeEmpty();
    }

    private static string Expected(bool cloudApp, string? requested, string signature, bool allowInBand)
    {
        if (!allowInBand || requested != "in_band")
        {
            return "out_of_band";
        }

        return cloudApp && signature != "valid" ? "401" : "in_band";
    }
}
