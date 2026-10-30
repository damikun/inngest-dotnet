using System.Text;

namespace Inngest.Tests.Webhooks;

// Anyone can send anything to a webhook endpoint before the first signature has been checked, so no verifier may throw
// for what a request contains.
public sealed class VerifierRobustnessTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private static readonly (string Name, IWebhookVerifier Verifier, string[] Headers)[] Verifiers =
    [
        ("standard", new StandardWebhooksVerifier("whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw"), ["webhook-id", "webhook-timestamp", "webhook-signature"]),
        ("svix", new StandardWebhooksVerifier("whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw"), ["svix-id", "svix-timestamp", "svix-signature"]),
        ("stripe", new StripeWebhookVerifier("whsec_secret"), ["Stripe-Signature"]),
        ("github", new GitHubWebhookVerifier("secret"), ["X-Hub-Signature-256", "X-GitHub-Delivery"]),
        ("shopify", new ShopifyWebhookVerifier("secret"), ["X-Shopify-Hmac-Sha256", "X-Shopify-Webhook-Id"]),
        (
            "hmac",
            new HmacWebhookVerifier(
                new HmacWebhookOptions
                {
                    SignatureHeader = "X-Signature",
                    SignaturePrefix = "v0=",
                    TimestampHeader = "X-Timestamp",
                    SignedContentPrefix = "v0:",
                    DeliveryIdHeader = "X-Delivery",
                },
                "secret"),
            ["X-Signature", "X-Timestamp", "X-Delivery"]),
    ];

    public static TheoryData<string> Names() => [.. Verifiers.Select(verifier => verifier.Name)];

    [Theory]
    [MemberData(nameof(Names))]
    public void Random_headers_and_bodies_never_throw_and_never_verify(string name)
    {
        var (_, verifier, headers) = Verifiers.Single(candidate => candidate.Name == name);
        var random = new Random(20261029);

        for (var round = 0; round < 3000; round++)
        {
            var body = new byte[random.Next(0, 64)];
            random.NextBytes(body);
            var request = new TestWebhookRequest(body);
            foreach (var header in headers)
            {
                if (random.Next(4) > 0)
                {
                    request.With(header, Garbage(random));
                }
            }

            var result = verifier.Verify(request, Now);

            result.IsValid.ShouldBeFalse();
            result.Reason.ShouldNotBeNullOrEmpty();
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Enormous_and_hostile_headers_are_rejected_in_bounded_time(string name)
    {
        var (_, verifier, headers) = Verifiers.Single(candidate => candidate.Name == name);
        var hostile = new[]
        {
            new string('v', 200_000),
            string.Join(' ', Enumerable.Repeat("v1,AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", 4000)),
            string.Join(',', Enumerable.Repeat("v1=00", 20_000)),
            new string('9', 100_000),
            "\0\0\0\0",
            "t=" + new string('9', 5000) + ",v1=" + new string('f', 5000),
            "\ud800\udc00 \udfff",
        };

        foreach (var value in hostile)
        {
            var request = new TestWebhookRequest(new byte[1024]);
            foreach (var header in headers)
            {
                request.With(header, value);
            }

            verifier.Verify(request, Now).IsValid.ShouldBeFalse();
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void A_request_with_no_headers_and_no_body_is_rejected(string name)
    {
        var (_, verifier, _) = Verifiers.Single(candidate => candidate.Name == name);

        verifier.Verify(new TestWebhookRequest(ReadOnlyMemory<byte>.Empty), Now).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void A_null_request_is_a_programming_error_and_says_so()
    {
        foreach (var (_, verifier, _) in Verifiers)
        {
            Should.Throw<ArgumentNullException>(() => verifier.Verify(null!, Now));
        }
    }

    private static string Garbage(Random random)
    {
        var length = random.Next(0, 90);
        var text = new StringBuilder(length);
        const string Alphabet = "0123456789abcdefABCDEFxyz=,;. +/-_v1t:\t";
        for (var i = 0; i < length; i++)
        {
            text.Append(random.Next(10) == 0 ? (char)random.Next(0x20, 0x2FF) : Alphabet[random.Next(Alphabet.Length)]);
        }

        return text.ToString();
    }
}
