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

    // "kind" is an ordinary property of Ring and the discriminator of Part, both of which the result holds.
    [Fact]
    public async Task A_property_named_like_the_discriminator_of_another_type_is_left_where_it_is()
    {
        var function = TestFunction.Define(async context =>
        {
            var drawing = await context.Step.RunAsync("draw", () => new Drawing(new Ring("x", 1), new Twig("b")));
            return $"{drawing.Figure} {drawing.Part}";
        });

        var result = await _host.CallAsync(function, Call.New().Data(
            "draw",
            """{"figure":{"kind":"x","size":1,"type":"ring"},"part":{"bark":"b","kind":"twig"}}"""));

        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe("\"Ring { Kind = x, Size = 1 } Twig { Bark = b }\"");
    }

    [Fact]
    public async Task The_items_of_a_polymorphic_list_are_read_back_from_sorted_keys()
    {
        var function = TestFunction.Define(async context =>
        {
            var history = await context.Step.RunAsync<MethodLog>("history", () => new MethodHistory { new BankTransfer("DE89") });
            return $"{history.GetType().Name} {string.Join(",", history.Select(Describe))}";
        });

        var result = await _host.CallAsync(function, Call.New().Data("history", """{"$type":"history","$values":[{"iban":"DE89","type":"bank"}]}"""));

        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe("\"MethodHistory bank DE89\"");
    }

    [Fact]
    public async Task A_property_with_a_converter_that_calls_the_serializer_is_read_back_from_sorted_keys()
    {
        var function = TestFunction.Define(async context =>
        {
            var boxed = await context.Step.RunAsync("box", () => new Boxed(new Card("4242", 2030)));
            return Describe(boxed.Item);
        });

        var result = await _host.CallAsync(function, Call.New().Data("box", """{"item":{"expYear":2030,"last4":"4242","type":"card"}}"""));

        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe("\"card 4242\"");
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

    [JsonPolymorphic]
    [JsonDerivedType(typeof(MethodHistory), "history")]
    public class MethodLog : List<PaymentMethod>;

    public sealed class MethodHistory : MethodLog;

    public sealed record Boxed([property: JsonConverter(typeof(PassThrough))] PaymentMethod Item);

    public sealed class PassThrough : JsonConverter<PaymentMethod>
    {
        public override PaymentMethod? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            JsonSerializer.Deserialize<PaymentMethod>(ref reader, options);

        public override void Write(Utf8JsonWriter writer, PaymentMethod value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value, options);
    }

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
    [JsonDerivedType(typeof(Ring), "ring")]
    public abstract record Figure;

    public sealed record Ring(string Kind, int Size) : Figure;

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
    [JsonDerivedType(typeof(Twig), "twig")]
    public abstract record Part;

    public sealed record Twig(string Bark) : Part;

    public sealed record Drawing(Figure Figure, Part Part);
}
