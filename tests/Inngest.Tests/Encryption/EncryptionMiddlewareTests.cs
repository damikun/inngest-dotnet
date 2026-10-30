using System.Security.Cryptography;
using System.Text.Json;
using Inngest.Encryption;

namespace Inngest.Tests.Encryption;

// What the middleware does with each kind of JSON, checked on the payload transforms against what the TypeScript
// package wrote, and against what the package does with the same values (its middleware ran on them to settle each
// of the cases below).
public sealed class EncryptionMiddlewareTests
{
    public static TheoryData<string> Names => new(JsVectors.LibSodium.Select(vector => vector.Name));

    // Values that carry the markers of an envelope but are not one, which the package takes for ordinary data.
    public static TheoryData<string> Lookalikes => new()
    {
        """{"__ENCRYPTED__":true,"__STRATEGY__":null,"data":"abc"}""",
        """{"__ENCRYPTED__":true,"__STRATEGY__":5,"data":"abc"}""",
        """{"__ENCRYPTED__":"true","__STRATEGY__":"inngest/libsodium","data":"abc"}""",
        """{"__ENCRYPTED__":false,"__STRATEGY__":"inngest/libsodium","data":"abc"}""",
        """{"__ENCRYPTED__":true,"__STRATEGY__":"inngest/libsodium"}""",
        """{"__ENCRYPTED__":true,"__STRATEGY__":"inngest/libsodium","data":5}""",
        """{"__STRATEGY__":"inngest/libsodium","data":"abc"}""",
        """[{"__ENCRYPTED__":true,"data":"abc"}]""",
        """{"inner":{"__ENCRYPTED__":true,"__STRATEGY__":"inngest/libsodium","data":"abc"}}""",
        "\"plain\"",
        "42",
        "null",
    };

    [Fact]
    public void An_event_the_typescript_package_encrypted_has_its_data_decrypted()
    {
        EncryptionHarness.Middleware().Event(JsVectors.EncryptedEvent).ShouldBeJson(JsVectors.Event);
    }

    [Fact]
    public void Every_field_of_the_data_that_holds_an_envelope_is_decrypted_whatever_it_is_called()
    {
        EncryptionHarness.Middleware().Event(JsVectors.EncryptedCustomFieldEvent).ShouldBeJson(JsVectors.Event);
    }

    [Fact]
    public void An_event_with_aes_envelopes_of_the_first_versions_is_decrypted()
    {
        EncryptionHarness.Middleware().Event(JsVectors.EncryptedAesEvent).ShouldBeJson(JsVectors.Event);
    }

    [Fact]
    public void The_rest_of_the_event_and_the_order_of_its_properties_are_kept()
    {
        var decrypted = EncryptionHarness.Middleware().Event(JsVectors.EncryptedEvent)!;

        using var document = JsonDocument.Parse(decrypted);
        document.RootElement.EnumerateObject().Select(property => property.Name).ShouldBe(["name", "data", "id", "ts"]);
        document.RootElement.GetProperty("data").EnumerateObject().Select(property => property.Name)
            .ShouldBe(["public_field", "count", "encrypted", "secret_data"]);
        document.RootElement.GetProperty("ts").GetRawText().ShouldBe("1793268000000");
    }

    [Theory]
    [InlineData("""{"name":"a/b","data":{"x":1,"y":{"z":2}},"id":"1"}""")]
    [InlineData("""{"name":"a/b","data":{}}""")]
    [InlineData("""{"name":"a/b","data":null}""")]
    [InlineData("""{"name":"a/b","data":[1,2]}""")]
    [InlineData("""{"name":"a/b","data":"text"}""")]
    [InlineData("""{"name":"a/b"}""")]
    [InlineData("null")]
    [InlineData("[]")]
    public void An_event_without_an_encrypted_field_is_kept_as_it_was(string json)
    {
        EncryptionHarness.Middleware().Event(json).ShouldBeNull();
    }

    [Theory]
    [MemberData(nameof(Lookalikes))]
    public void A_value_that_only_looks_like_an_envelope_is_kept(string value)
    {
        var middleware = EncryptionHarness.Middleware();

        middleware.Event($$$"""{"name":"a/b","data":{"field":{{{value}}}}}""").ShouldBeNull();
        middleware.Result(value).ShouldBeNull();
    }

    [Fact]
    public void An_event_with_a_field_that_no_key_decrypts_fails()
    {
        var middleware = EncryptionHarness.Middleware(options => options.Key = "another-key");

        var failure = Should.Throw<CryptographicException>(() => middleware.Event(JsVectors.EncryptedEvent));

        failure.Message.ShouldBe("The encrypted value could not be decrypted with any of the configured keys.");
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void A_step_result_the_typescript_package_encrypted_is_decrypted_to_what_it_encrypted(string name)
    {
        var vector = JsVectors.Find(JsVectors.LibSodium, name);

        EncryptionHarness.Middleware().Result(vector.Envelope).ShouldBe(vector.Plaintext);
    }

    [Fact]
    public void A_result_encrypted_with_a_fallback_key_is_decrypted_after_a_rotation()
    {
        var middleware = EncryptionHarness.Middleware(options =>
        {
            options.Key = JsVectors.SecondKey;
            options.FallbackDecryptionKeys = [JsVectors.Key];
        });

        middleware.Result(JsVectors.LibSodium[0].Envelope).ShouldBe(JsVectors.LibSodium[0].Plaintext);
        middleware.Result(JsVectors.SecondKeyEnvelope).ShouldBe("""{"rotated":true}""");
    }

    [Fact]
    public void Aes_envelopes_are_decrypted_with_the_same_keys_and_fallbacks()
    {
        var middleware = EncryptionHarness.Middleware(options => options.FallbackDecryptionKeys = [JsVectors.SecondKey]);

        middleware.Result(JsVectors.AesV0[0].Envelope).ShouldBe(JsVectors.AesV0[0].Plaintext);
        middleware.Result(JsVectors.AesStrategy[0].Envelope).ShouldBe(JsVectors.AesStrategy[0].Plaintext);
        middleware.Result(JsVectors.SecondKeyAesEnvelope).ShouldBe("""{"rotated":true}""");
    }

    [Fact]
    public void The_event_a_wait_matched_has_its_data_decrypted_and_no_other_result_is_taken_for_an_event()
    {
        var middleware = EncryptionHarness.Middleware();

        middleware.Result(JsVectors.EncryptedEvent, StepType.WaitForEvent).ShouldBeJson(JsVectors.Event);
        middleware.Result(JsVectors.EncryptedEvent, StepType.Run).ShouldBeNull();
        middleware.Result("null", StepType.WaitForEvent).ShouldBeNull();
    }

    [Fact]
    public void A_result_that_an_invoked_function_encrypted_as_a_whole_is_decrypted()
    {
        EncryptionHarness.Middleware().Result(JsVectors.LibSodium[1].Envelope, StepType.Invoke).ShouldBe("\"s3cret\"");
    }

    [Fact]
    public void An_output_is_encrypted_as_a_whole_into_the_envelope_of_the_typescript_package()
    {
        var output = EncryptionHarness.Middleware().StepOutput("""{"hello":"world"}""")!;

        using var document = JsonDocument.Parse(output);
        var properties = document.RootElement.EnumerateObject().ToArray();
        properties.Select(property => property.Name).ShouldBe(["__ENCRYPTED__", "__STRATEGY__", "data"]);
        properties[0].Value.GetBoolean().ShouldBeTrue();
        properties[1].Value.GetString().ShouldBe("inngest/libsodium");
        output.ShouldStartWith("""{"__ENCRYPTED__":true,"__STRATEGY__":"inngest/libsodium","data":""");
        EncryptionHarness.Decrypt(new LibSodiumEncryptionService([JsVectors.Key]), output).ShouldBe("""{"hello":"world"}""");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("\"\"")]
    [InlineData("\"s3cret\"")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("[1,\"two\",{\"three\":3},null,true]")]
    [InlineData("12345678901234567890.123456789")]
    [InlineData("{\"text\":\"héllo wörld ✓\"}")]
    public void A_step_output_or_function_output_of_any_kind_is_encrypted_and_decrypts_to_itself(string json)
    {
        var middleware = EncryptionHarness.Middleware();
        var service = new LibSodiumEncryptionService([JsVectors.Key]);

        EncryptionHarness.Decrypt(service, middleware.StepOutput(json)!).ShouldBeJson(json);
        EncryptionHarness.Decrypt(service, middleware.FunctionOutput(json)!).ShouldBeJson(json);
    }

    [Fact]
    public void An_output_that_decrypts_is_given_back_exactly_as_it_was_encrypted()
    {
        var middleware = EncryptionHarness.Middleware();
        var json = """{"id":9007199254740993,"price":1.10,"tags":["a","b"],"note":"he said \"hi\" \\ done"}""";

        middleware.Result(middleware.StepOutput(json)!).ShouldBe(json);
    }

    [Fact]
    public void Null_has_nothing_to_hide_and_is_not_encrypted()
    {
        var middleware = EncryptionHarness.Middleware();

        middleware.StepOutput("null").ShouldBeNull();
        middleware.FunctionOutput("null").ShouldBeNull();
    }

    [Fact]
    public void Decrypt_only_leaves_outputs_alone_and_still_decrypts()
    {
        var middleware = EncryptionHarness.Middleware(options => options.DecryptOnly = true);

        middleware.StepOutput("""{"a":1}""").ShouldBeNull();
        middleware.FunctionOutput("""{"a":1}""").ShouldBeNull();
        middleware.Result(JsVectors.LibSodium[0].Envelope).ShouldBe(JsVectors.LibSodium[0].Plaintext);
        middleware.Event(JsVectors.EncryptedEvent).ShouldBeJson(JsVectors.Event);
    }

    [Fact]
    public void Only_the_first_key_encrypts()
    {
        var middleware = EncryptionHarness.Middleware(options =>
        {
            options.Key = JsVectors.SecondKey;
            options.FallbackDecryptionKeys = [JsVectors.Key];
        });

        var output = middleware.StepOutput("1")!;

        EncryptionHarness.Decrypt(new LibSodiumEncryptionService([JsVectors.SecondKey]), output).ShouldBe("1");
        Should.Throw<CryptographicException>(() => EncryptionHarness.Decrypt(new LibSodiumEncryptionService([JsVectors.Key]), output));
    }

    [Fact]
    public void Aes_envelopes_need_a_key_to_decrypt_and_a_strategy_nobody_serves_is_refused()
    {
        var middleware = EncryptionHarness.Middleware(options =>
        {
            options.Key = null;
            options.Service = new ReversingService();
        });

        Should.Throw<CryptographicException>(() => middleware.Result(JsVectors.AesV0[0].Envelope))
            .Message.ShouldBe("The value is encrypted with the legacy AES format, which needs EncryptionOptions.Key to decrypt.");
        Should.Throw<CryptographicException>(() => middleware.Result(JsVectors.LibSodium[0].Envelope))
            .Message.ShouldBe("""The value is encrypted with the strategy "inngest/libsodium", which needs EncryptionOptions.Key to decrypt.""");
        Should.Throw<CryptographicException>(() => middleware.Result("""{"__ENCRYPTED__":true,"__STRATEGY__":"other/thing","data":"x"}"""))
            .Message.ShouldBe("""No encryption service handles the strategy "other/thing".""");
    }

    [Fact]
    public void Keys_are_not_in_the_errors()
    {
        var middleware = EncryptionHarness.Middleware(options => options.Key = "very-secret-key-material");

        var failure = Should.Throw<CryptographicException>(() => middleware.Result(JsVectors.LibSodium[0].Envelope));

        failure.ToString().ShouldNotContain("very-secret-key-material");
    }

    // Reverses the bytes, which is no encryption at all but is easy to tell from the default strategy.
    internal sealed class ReversingService : EncryptionService
    {
        public override string Identifier => "test/reversing";

        public override string Encrypt(ReadOnlySpan<byte> json) => Convert.ToBase64String(json.ToArray().Reverse().ToArray());

        public override byte[] Decrypt(string data) => Convert.FromBase64String(data).Reverse().ToArray();
    }
}
