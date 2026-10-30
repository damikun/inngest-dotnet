using System.Text;
using Inngest.Tests.Execution;
using Inngest.Webhooks;
using Microsoft.Extensions.Logging.Testing;

namespace Inngest.Tests.Webhooks;

// The pipeline behind a webhook endpoint for any host: verify, parse, make the event, send it, say what to answer.
public sealed class WebhookReceiverTests
{
    private const string Secret = "whsec_Hk3pX9wZq2LmN7vR4tYb8cDf6gJa1sE5";
    private const string Body = """{"id":"evt_1","type":"invoice.paid","data":{"object":{"id":"in_1","note":"private-note-7731"}}}""";

    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private readonly TestTimeProvider _time = new(Now);
    private readonly FakeLogger _logger = new();
    private readonly RecordingInngestClient _client = new();

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_verified_delivery_is_sent_as_the_event_of_its_provider_and_answered_with_200()
    {
        var outcome = await Receiver().ReceiveAsync(StripeRequest(), Cancellation);

        outcome.ShouldBe(WebhookOutcome.Accepted);
        outcome.StatusCode().ShouldBe(200);
        var sent = _client.Sent.ShouldHaveSingleItem();
        sent.Name.ShouldBe("stripe/invoice.paid");
        sent.Id.ShouldBe("evt_1");
        sent.DataJson.ShouldBe(Body);
    }

    [Fact]
    public async Task A_delivery_sent_again_is_sent_again_with_the_same_id_for_Inngest_to_drop()
    {
        var receiver = Receiver();

        await receiver.ReceiveAsync(StripeRequest(), Cancellation);
        _time.Advance(TimeSpan.FromMinutes(2));
        await receiver.ReceiveAsync(StripeRequest(), Cancellation);

        _client.Sent.Select(sent => sent.Id).ShouldBe(["evt_1", "evt_1"]);
    }

    [Fact]
    public async Task A_signature_that_does_not_match_sends_nothing_and_the_body_is_not_even_parsed()
    {
        var request = new TestWebhookRequest("this is not json").With("Stripe-Signature", $"t={Now.ToUnixTimeSeconds()},v1={new string('0', 64)}");

        var outcome = await Receiver().ReceiveAsync(request, Cancellation);

        outcome.ShouldBe(WebhookOutcome.Unauthorized);
        outcome.StatusCode().ShouldBe(401);
        _client.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_valid_signature_over_a_body_that_is_not_json_is_a_bad_request()
    {
        var request = SignedStripe("not json at all");

        var outcome = await Receiver().ReceiveAsync(request, Cancellation);

        outcome.ShouldBe(WebhookOutcome.BadRequest);
        outcome.StatusCode().ShouldBe(400);
        _client.Sent.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("[1,2]")]
    [InlineData("{}")]
    [InlineData("""{"id":"evt_1"}""")]
    [InlineData("""{"type":"a b"}""")]
    public async Task A_valid_signature_over_a_body_no_event_can_be_made_of_is_a_bad_request(string body)
    {
        var outcome = await Receiver().ReceiveAsync(SignedStripe(body), Cancellation);

        outcome.ShouldBe(WebhookOutcome.BadRequest);
        _client.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_body_over_the_limit_is_refused_before_anything_is_verified()
    {
        var verified = 0;
        var provider = new WebhookProvider("counting", new DelegateVerifier(_ =>
        {
            verified++;
            return WebhookVerification.Valid();
        }), _ => null);
        var receiver = Receiver(provider, new WebhookOptions { MaxBodySize = 10 });

        var outcome = await receiver.ReceiveAsync(new TestWebhookRequest(new string('x', 11)), Cancellation);

        outcome.ShouldBe(WebhookOutcome.PayloadTooLarge);
        outcome.StatusCode().ShouldBe(413);
        verified.ShouldBe(0);
        (await receiver.ReceiveAsync(new TestWebhookRequest(new string('x', 10)), Cancellation)).ShouldBe(WebhookOutcome.BadRequest);
        verified.ShouldBe(1);
    }

    [Fact]
    public async Task The_transform_can_change_the_name_the_data_and_the_id()
    {
        var options = new WebhookOptions
        {
            Transform = (delivery, @event) => @event with
            {
                Name = "billing/" + @event.Name["stripe/".Length..],
                Id = delivery.Provider + ":" + @event.Id,
                Data = new { invoice = "in_1" },
            },
        };

        var outcome = await Receiver(options: options).ReceiveAsync(StripeRequest(), Cancellation);

        outcome.ShouldBe(WebhookOutcome.Accepted);
        var sent = _client.Sent.ShouldHaveSingleItem();
        sent.Name.ShouldBe("billing/invoice.paid");
        sent.Id.ShouldBe("stripe:evt_1");
        sent.DataJson.ShouldBe("""{"invoice":"in_1"}""");
    }

    [Fact]
    public async Task The_transform_sees_the_verified_delivery()
    {
        WebhookDelivery? seen = null;
        string? name = null;
        var options = new WebhookOptions
        {
            Transform = (delivery, @event) =>
            {
                seen = delivery;
                name = delivery.Payload.GetProperty("type").GetString();
                return @event;
            },
        };
        var request = StripeRequest().With("X-Custom", "yes");

        await Receiver(options: options).ReceiveAsync(request, Cancellation);

        seen.ShouldNotBeNull();
        seen.Provider.ShouldBe("stripe");
        seen.GetHeader("x-custom").ShouldBe("yes");
        name.ShouldBe("invoice.paid");
    }

    [Fact]
    public async Task A_transform_that_returns_no_event_skips_the_delivery_with_a_success()
    {
        var options = new WebhookOptions { Transform = (_, @event) => @event.Name == "stripe/invoice.paid" ? null : @event };

        var outcome = await Receiver(options: options).ReceiveAsync(StripeRequest(), Cancellation);

        outcome.ShouldBe(WebhookOutcome.Skipped);
        outcome.StatusCode().ShouldBe(204);
        _client.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_transform_that_throws_fails_the_delivery_so_that_the_provider_tries_again()
    {
        var options = new WebhookOptions { Transform = (_, _) => throw new InvalidOperationException("the secret order total is 4311") };

        var outcome = await Receiver(options: options).ReceiveAsync(StripeRequest(), Cancellation);

        outcome.ShouldBe(WebhookOutcome.Failed);
        outcome.StatusCode().ShouldBe(500);
        _client.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_verifier_that_throws_fails_the_delivery_instead_of_letting_it_through()
    {
        var provider = new WebhookProvider("buggy", new DelegateVerifier(_ => throw new InvalidOperationException("bug")), _ => new InngestEvent("x/y"));

        var outcome = await Receiver(provider).ReceiveAsync(new TestWebhookRequest("{}"), Cancellation);

        outcome.ShouldBe(WebhookOutcome.Failed);
        _client.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_send_that_fails_is_answered_with_503_so_that_the_provider_delivers_it_again()
    {
        _client.Fail = new InngestException("Inngest API error: 500 Internal server error", 500);

        var outcome = await Receiver().ReceiveAsync(StripeRequest(), Cancellation);

        outcome.ShouldBe(WebhookOutcome.Unavailable);
        outcome.StatusCode().ShouldBe(503);
        _logger.Collector.GetSnapshot().ShouldContain(record => record.Level == Microsoft.Extensions.Logging.LogLevel.Error && record.Exception == _client.Fail);
    }

    [Fact]
    public async Task An_event_the_client_refuses_is_a_bad_request_because_sending_it_again_would_fail_the_same_way()
    {
        _client.Fail = new ArgumentException("The event name 'inngest/x' is reserved.");

        var outcome = await Receiver().ReceiveAsync(StripeRequest(), Cancellation);

        outcome.ShouldBe(WebhookOutcome.BadRequest);
    }

    [Fact]
    public async Task A_send_that_takes_longer_than_the_timeout_is_cancelled_and_answered_with_503()
    {
        _client.Block = true;
        var receiver = Receiver(options: new WebhookOptions { SendTimeout = TimeSpan.FromSeconds(4) });

        var pending = receiver.ReceiveAsync(StripeRequest(), Cancellation).AsTask();
        await _time.TimersCreated(1);
        pending.IsCompleted.ShouldBeFalse();
        _time.Advance(TimeSpan.FromSeconds(4));

        (await pending).ShouldBe(WebhookOutcome.Unavailable);
        _client.Cancelled.ShouldBeTrue();
    }

    [Fact]
    public async Task A_provider_that_hangs_up_cancels_the_send_and_the_cancellation_reaches_the_host()
    {
        _client.Block = true;
        using var hangUp = new CancellationTokenSource();
        var pending = Receiver().ReceiveAsync(StripeRequest(), hangUp.Token).AsTask();
        await _time.TimersCreated(1);

        await hangUp.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Nothing_that_is_logged_holds_a_secret_a_signature_or_a_body()
    {
        var receiver = Receiver();
        var signedRequest = StripeRequest();
        var signature = signedRequest.GetHeader("Stripe-Signature")!;

        await receiver.ReceiveAsync(signedRequest, Cancellation);
        await receiver.ReceiveAsync(new TestWebhookRequest(Body).With("Stripe-Signature", signature.Replace("v1=", "v1=0", StringComparison.Ordinal)), Cancellation);
        await receiver.ReceiveAsync(SignedStripe("not json private-note-7731"), Cancellation);
        _client.Fail = new InngestException("Inngest API error: 500", 500);
        await receiver.ReceiveAsync(StripeRequest(), Cancellation);
        _client.Fail = null;
        await Receiver(options: new WebhookOptions { MaxBodySize = 5 }).ReceiveAsync(StripeRequest(), Cancellation);
        await Receiver(options: new WebhookOptions { Transform = (_, _) => throw new InvalidOperationException("boom") }).ReceiveAsync(StripeRequest(), Cancellation);

        var logged = string.Join('\n', _logger.Collector.GetSnapshot().Select(record => record.Message + " " + record.Exception));
        logged.ShouldNotBeEmpty();
        logged.ShouldNotContain(Secret);
        logged.ShouldNotContain("Hk3pX9wZq2LmN7vR4tYb8cDf6gJa1sE5");
        logged.ShouldNotContain("private-note-7731");
        logged.ShouldNotContain(signature);
        logged.ShouldNotContain(signature[(signature.IndexOf("v1=", StringComparison.Ordinal) + 3)..]);
    }

    [Fact]
    public void Options_that_cannot_work_are_refused_when_the_endpoint_is_set_up()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Receiver(options: new WebhookOptions { MaxBodySize = 0 }));
        Should.Throw<ArgumentOutOfRangeException>(() => Receiver(options: new WebhookOptions { SendTimeout = TimeSpan.Zero }));
        Should.Throw<ArgumentOutOfRangeException>(() => Receiver(options: new WebhookOptions { SendTimeout = Timeout.InfiniteTimeSpan }));
        Should.Throw<ArgumentOutOfRangeException>(() => Receiver(options: new WebhookOptions { SendTimeout = TimeSpan.FromDays(100) }));
    }

    private WebhookReceiver Receiver(WebhookProvider? provider = null, WebhookOptions? options = null) =>
        new(provider ?? WebhookProvider.Stripe(Secret), _client, options ?? new WebhookOptions(), _time, _logger);

    private TestWebhookRequest StripeRequest() => SignedStripe(Body);

    private TestWebhookRequest SignedStripe(string body) =>
        new TestWebhookRequest(body).With("Stripe-Signature", Signing.StripeHeader(Secret, _time.GetUtcNow().ToUnixTimeSeconds(), Encoding.UTF8.GetBytes(body)));

    private sealed class DelegateVerifier(Func<IWebhookRequest, WebhookVerification> verify) : IWebhookVerifier
    {
        public WebhookVerification Verify(IWebhookRequest request, DateTimeOffset now) => verify(request);
    }
}
