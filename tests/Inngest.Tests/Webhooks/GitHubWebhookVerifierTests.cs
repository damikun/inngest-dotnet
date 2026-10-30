using System.Text;

namespace Inngest.Tests.Webhooks;

public sealed class GitHubWebhookVerifierTests
{
    // The example of "Validating webhook deliveries" in GitHub's documentation.
    private const string Secret = "It's a Secret to Everybody";
    private const string Body = "Hello, World!";
    private const string Signature = "sha256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17";

    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private static TestWebhookRequest Signed(string signature = Signature, string body = Body) =>
        new TestWebhookRequest(body).With("X-Hub-Signature-256", signature);

    [Fact]
    public void The_published_example_verifies()
    {
        var result = new GitHubWebhookVerifier(Secret).Verify(Signed(), Now);

        result.IsValid.ShouldBeTrue();
        result.DeliveryId.ShouldBeNull();
    }

    [Fact]
    public void The_delivery_id_is_the_header_GitHub_sends()
    {
        var request = Signed().With("X-GitHub-Delivery", "72d3162e-cc78-11e3-81ab-4c9367dc0958");

        new GitHubWebhookVerifier(Secret).Verify(request, Now).DeliveryId.ShouldBe("72d3162e-cc78-11e3-81ab-4c9367dc0958");
    }

    [Theory]
    [InlineData("two words")]
    [InlineData("tab\tinside")]
    [InlineData("mésg")]
    [InlineData("")]
    public void A_delivery_id_that_is_not_a_short_visible_token_is_not_used(string id)
    {
        var request = Signed().With("X-GitHub-Delivery", id);

        var result = new GitHubWebhookVerifier(Secret).Verify(request, Now);

        result.IsValid.ShouldBeTrue();
        result.DeliveryId.ShouldBeNull();
    }

    [Fact]
    public void There_is_no_timestamp_to_check_so_the_time_of_arrival_does_not_matter()
    {
        new GitHubWebhookVerifier(Secret).Verify(Signed(), Now.AddYears(3)).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void The_scheme_prefix_is_not_case_sensitive_and_the_hex_may_be_in_either_case()
    {
        var verifier = new GitHubWebhookVerifier(Secret);

        verifier.Verify(Signed("SHA256=" + Signature["sha256=".Length..].ToUpperInvariant()), Now).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void An_empty_body_has_a_signature_too()
    {
        var signature = "sha256=66a0c074deaa0f489ead6537e0d32f9a344b90bbeda705b6ed45ecd3b413fb40";

        new GitHubWebhookVerifier(Secret).Verify(Signed(signature, body: ""), Now).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void A_changed_body_or_a_wrong_secret_does_not_verify()
    {
        new GitHubWebhookVerifier(Secret).Verify(Signed(body: "Hello, World?"), Now).IsValid.ShouldBeFalse();
        new GitHubWebhookVerifier(Secret + " ").Verify(Signed(), Now).IsValid.ShouldBeFalse();
        new GitHubWebhookVerifier("another secret").Verify(Signed(), Now).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Either_of_two_secrets_passes_while_a_secret_is_replaced()
    {
        var verifier = new GitHubWebhookVerifier(["the new secret", Secret]);

        verifier.Verify(Signed(), Now).IsValid.ShouldBeTrue();
        verifier.Verify(Signed(body: "x"), Now).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void The_sha1_header_is_ignored()
    {
        // X-Hub-Signature is the SHA-1 HMAC of the same body: GitHub sends it for compatibility only.
        var request = new TestWebhookRequest(Body).With("X-Hub-Signature", "sha1=01dc10d0c83e72ed246219cdd91669667fe2ca59");

        var result = new GitHubWebhookVerifier(Secret).Verify(request, Now);

        result.IsValid.ShouldBeFalse();
        result.Reason.ShouldNotBeNull().ShouldContain("missing");
    }

    [Theory]
    [InlineData("")]
    [InlineData("sha256=")]
    [InlineData("757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17")]
    [InlineData("sha1=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17")]
    [InlineData("sha256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e1")]
    [InlineData("sha256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e1700")]
    [InlineData("sha256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043eZZ")]
    [InlineData("sha256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17=")]
    [InlineData("sha256=🙂🙂")]
    public void A_malformed_signature_header_is_rejected_without_an_exception(string header)
    {
        new GitHubWebhookVerifier(Secret).Verify(Signed(header), Now).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void A_bad_secret_fails_the_constructor()
    {
        Should.Throw<ArgumentException>(() => new GitHubWebhookVerifier(""));
        Should.Throw<ArgumentException>(() => new GitHubWebhookVerifier(Array.Empty<string>()));
    }

    [Fact]
    public void The_test_helper_agrees_with_GitHubs_example()
    {
        Signing.Hex(System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(Body)))
            .ShouldBe(Signature["sha256=".Length..]);
    }
}
