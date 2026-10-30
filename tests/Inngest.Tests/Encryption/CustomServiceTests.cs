using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Inngest.Tests.Encryption;

// An EncryptionService of the app's own takes the place of the default strategy, as the encryptionService option does
// in the TypeScript package, and an envelope names it by its identifier.
public sealed class CustomServiceTests
{
    [Fact]
    public void The_service_encrypts_outputs_into_envelopes_that_name_it()
    {
        var middleware = EncryptionHarness.Middleware(options => options.Service = new ReversingService());

        var output = middleware.StepOutput("""{"a":1}""")!;

        output.ShouldBe("""{"__ENCRYPTED__":true,"__STRATEGY__":"test/reversing","data":"fTE6ImEiew=="}""");
        middleware.Result(output).ShouldBe("""{"a":1}""");
    }

    [Fact]
    public async Task The_service_encrypts_events_too()
    {
        var middleware = EncryptionHarness.Middleware(options => options.Service = new ReversingService());
        var original = new InngestEvent("app/a", new { Encrypted = "x" });
        IReadOnlyList<InngestEvent>? sent = null;

        await middleware.WrapSendEventAsync(
            null,
            [original],
            next =>
            {
                sent = next;
                return Task.FromResult(new SendEventsResult([]));
            },
            TestContext.Current.CancellationToken);

        var json = JsonSerializer.Serialize(sent.ShouldNotBeNull().Single().Data);
        json.ShouldBe("""{"encrypted":{"__ENCRYPTED__":true,"__STRATEGY__":"test/reversing","data":"Ingi"}}""");
    }

    [Fact]
    public void The_default_strategies_are_still_read_when_a_key_is_set_so_that_data_can_move_to_the_service()
    {
        var middleware = EncryptionHarness.Middleware(options => options.Service = new ReversingService());

        middleware.Result(JsVectors.LibSodium[0].Envelope).ShouldBe(JsVectors.LibSodium[0].Plaintext);
        middleware.Result(JsVectors.AesV0[0].Envelope).ShouldBe(JsVectors.AesV0[0].Plaintext);
        middleware.Event(JsVectors.EncryptedEvent).ShouldBeJson(JsVectors.Event);
    }

    [Fact]
    public void The_identifier_decides_which_service_decrypts()
    {
        var middleware = EncryptionHarness.Middleware(options => options.Service = new FailingService());

        // An envelope of the default strategy is not handed to a service that has another identifier.
        middleware.Result(JsVectors.LibSodium[1].Envelope).ShouldBe("\"s3cret\"");
        Should.Throw<InvalidOperationException>(() => middleware.Result("""{"__ENCRYPTED__":true,"__STRATEGY__":"test/failing","data":"x"}"""));
    }

    [Fact]
    public void An_error_of_the_service_reaches_the_caller()
    {
        var middleware = EncryptionHarness.Middleware(options => options.Service = new FailingService());

        Should.Throw<InvalidOperationException>(() => middleware.StepOutput("1")).Message.ShouldBe("The vault is down.");
        Should.Throw<InvalidOperationException>(() => middleware.Result("""{"__ENCRYPTED__":true,"__STRATEGY__":"test/failing","data":"x"}""")).Message.ShouldBe("The vault is down.");
    }

    [Fact]
    public void The_service_is_given_the_json_of_the_value_as_utf8_and_what_it_returns_is_read_as_json()
    {
        var recording = new RecordingService();
        var middleware = EncryptionHarness.Middleware(options => options.Service = recording);

        middleware.StepOutput("""{"text":"h\u00e9llo \ud83d\ude00"}""");

        // Text is not escaped, except for characters outside the basic plane, which JSON.parse reads back as they were.
        using var json = JsonDocument.Parse(recording.Encrypted.ShouldHaveSingleItem());
        json.RootElement.GetProperty("text").GetString().ShouldBe("h\u00e9llo \ud83d\ude00");
        Encoding.UTF8.GetString(recording.Encrypted[0]).ShouldStartWith("{\"text\":\"h\u00e9llo ");
        Should.Throw<CryptographicException>(() => middleware.Result("""{"__ENCRYPTED__":true,"__STRATEGY__":"test/recording","data":"not json"}"""))
            .Message.ShouldBe("The decrypted value is not valid JSON.");
    }

    private sealed class FailingService : EncryptionService
    {
        public override string Identifier => "test/failing";

        public override string Encrypt(ReadOnlySpan<byte> json) => throw new InvalidOperationException("The vault is down.");

        public override byte[] Decrypt(string data) => throw new InvalidOperationException("The vault is down.");
    }

    private sealed class RecordingService : EncryptionService
    {
        public List<byte[]> Encrypted { get; } = [];

        public override string Identifier => "test/recording";

        public override string Encrypt(ReadOnlySpan<byte> json)
        {
            Encrypted.Add(json.ToArray());
            return "recorded";
        }

        public override byte[] Decrypt(string data) => Encoding.UTF8.GetBytes(data);
    }
}
