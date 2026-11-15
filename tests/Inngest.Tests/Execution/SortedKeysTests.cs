using System.Text.Json;
using System.Text.Json.Serialization;
using Inngest.Configuration;
using Inngest.Json;

namespace Inngest.Tests.Execution;

// Inngest sends every call body with the keys of its objects sorted (RFC 8785), so a type discriminator other than
// "$type" usually arrives after the properties it describes.
public sealed class SortedKeysTests : IDisposable
{
    private static readonly InngestSettings Settings = EngineHost.Settings with
    {
        SerializerOptions = SerializerOptionsSetup.Prepare(new JsonSerializerOptions(JsonSerializerDefaults.Web)),
    };

    private readonly EngineHost _host = new(settings: Settings);

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task A_step_result_with_a_custom_discriminator_is_read_back_from_sorted_keys()
    {
        var function = TestFunction.Define(async context =>
        {
            var method = await context.Step.RunAsync<PaymentMethod>("pick", () => new Card("4242", 2030));
            return Describe(method);
        });

        var result = await _host.CallAsync(function, Call.New().Data("pick", """{"expYear":2030,"last4":"4242","type":"card"}"""));

        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe("\"card 4242\"");
    }

    [Fact]
    public async Task Discriminators_nested_in_lists_and_properties_are_read_back_from_sorted_keys()
    {
        var function = TestFunction.Define(async context =>
        {
            var wallet = await context.Step.RunAsync("wallet", () => new Wallet("w1", [new Card("4242", 2030)], new Dictionary<string, PaymentMethod>()));
            return string.Join(",", [.. wallet.Methods.Select(Describe), .. wallet.ByName.Select(pair => $"{pair.Key}={Describe(pair.Value)}")]);
        });

        var result = await _host.CallAsync(function, Call.New().Data(
            "wallet",
            """{"byName":{"main":{"iban":"DE89","type":"bank"}},"id":"w1","methods":[{"iban":"DE00","type":"bank"},{"expYear":2030,"last4":"4242","type":"card"}]}"""));

        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe("\"bank DE00,card 4242,main=bank DE89\"");
    }

    [Fact]
    public async Task Event_data_with_a_custom_discriminator_is_read_from_sorted_keys()
    {
        var function = TestFunction.Define<OrderPlaced>(context => Task.FromResult<object?>(Describe(context.Event.Data.Payment)));

        var result = await _host.CallAsync(function, Call.New().Event(
            """{"data":{"orderId":"o1","payment":{"expYear":2030,"last4":"1111","type":"card"}},"id":"01JTEST","name":"test/event","ts":1793268000000,"user":{}}"""));

        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe("\"card 1111\"");
    }

    [Fact]
    public async Task An_invoke_result_with_a_custom_discriminator_is_read_back_from_sorted_keys()
    {
        var function = TestFunction.Define(async context =>
        {
            var method = await context.Step.InvokeAsync<PaymentMethod>("pick", new InvokeOptions("pick-method"));
            return Describe(method);
        });

        var result = await _host.CallAsync(function, Call.New().Data("pick", """{"iban":"DE89","type":"bank"}"""));

        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe("\"bank DE89\"");
    }

    [Fact]
    public void The_options_of_the_app_are_left_as_they_are()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        SerializerOptionsSetup.Prepare(options);

#if NET9_0_OR_GREATER
        options.AllowOutOfOrderMetadataProperties.ShouldBeFalse();
#endif
        options.IsReadOnly.ShouldBeFalse();
    }

    private static string Describe(PaymentMethod method) => method switch
    {
        Card card => $"card {card.Last4}",
        BankTransfer bank => $"bank {bank.Iban}",
        _ => "unknown",
    };

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
    [JsonDerivedType(typeof(Card), "card")]
    [JsonDerivedType(typeof(BankTransfer), "bank")]
    public abstract record PaymentMethod;

    public sealed record Card(string Last4, int ExpYear) : PaymentMethod;

    public sealed record BankTransfer(string Iban) : PaymentMethod;

    public sealed record Wallet(string Id, List<PaymentMethod> Methods, Dictionary<string, PaymentMethod> ByName);

    public sealed record OrderPlaced(string OrderId, PaymentMethod Payment);
}
