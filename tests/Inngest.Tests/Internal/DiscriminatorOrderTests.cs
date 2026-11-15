using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Inngest.Json;

namespace Inngest.Tests.Internal;

// What .NET 8 reads Inngest's sorted JSON with. It runs on every runtime, so that it is tested on the latest too.
public sealed class DiscriminatorOrderTests
{
    // Without AllowOutOfOrderMetadataProperties, like System.Text.Json 8.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    [Fact]
    public void Discriminators_move_to_the_front_of_their_objects_at_every_depth()
    {
        const string Sorted =
            """{"byName":{"main":{"iban":"DE89","type":"bank"}},"id":"w1","methods":[{"expYear":2030,"last4":"4242","type":"card"}],"nested":{"inner":{"kind":"leaf","name":"x"},"label":"n","type":"group"}}""";

        var moved = MoveFirst<Wallet>(Sorted);

        moved.GetRawText().ShouldBe(
            """{"byName":{"main":{"type":"bank","iban":"DE89"}},"id":"w1","methods":[{"type":"card","expYear":2030,"last4":"4242"}],"nested":{"type":"group","inner":{"kind":"leaf","name":"x"},"label":"n"}}""");
        var wallet = moved.Deserialize<Wallet>(Options)!;
        wallet.Methods.ShouldHaveSingleItem().ShouldBe(new Card("4242", 2030));
        wallet.ByName["main"].ShouldBe(new BankTransfer("DE89"));
        wallet.Nested.ShouldBeOfType<Group>().Inner.ShouldBe(new Leaf("x"));
    }

    [Fact]
    public void Sorted_keys_are_what_System_Text_Json_8_cannot_read()
    {
        Should.Throw<NotSupportedException>(() => JsonDocument.Parse("""{"expYear":2030,"last4":"4242","type":"card"}""").RootElement.Deserialize<PaymentMethod>(Options));
    }

    [Theory]
    [InlineData("""{"type":"card","expYear":2030,"last4":"4242"}""")]
    [InlineData("""{"$type":"x","b":1}""")]
    [InlineData("[]")]
    [InlineData("null")]
    public void A_value_whose_discriminators_already_lead_is_returned_as_it_is(string json)
    {
        MoveFirst<PaymentMethod>(json).GetRawText().ShouldBe(json);
    }

    [Fact]
    public void A_type_without_polymorphism_reads_its_value_as_it_is()
    {
        const string Json = """{"total":5,"type":"invoice"}""";

        MoveFirst<Invoice>(Json).GetRawText().ShouldBe(Json);
    }

    [Fact]
    public void The_default_discriminator_moves_too()
    {
        MoveFirst<Shape>("""{"!":1,"$type":"circle","radius":2}""").GetRawText().ShouldBe("""{"$type":"circle","!":1,"radius":2}""");
    }

    private static JsonElement MoveFirst<T>(string json)
    {
        using var document = JsonDocument.Parse(json);
        return DiscriminatorOrder.MoveFirst(document.RootElement, Options.GetTypeInfo(typeof(T))).Clone();
    }

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
    [JsonDerivedType(typeof(Card), "card")]
    [JsonDerivedType(typeof(BankTransfer), "bank")]
    public abstract record PaymentMethod;

    public sealed record Card(string Last4, int ExpYear) : PaymentMethod;

    public sealed record BankTransfer(string Iban) : PaymentMethod;

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
    [JsonDerivedType(typeof(Group), "group")]
    public abstract record Node;

    public sealed record Group(string Label, Part Inner) : Node;

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
    [JsonDerivedType(typeof(Leaf), "leaf")]
    public abstract record Part;

    public sealed record Leaf(string Name) : Part;

    public sealed record Wallet(string Id, List<PaymentMethod> Methods, Dictionary<string, PaymentMethod> ByName, Node Nested);

    public sealed record Invoice(string Type, int Total);

    [JsonPolymorphic]
    [JsonDerivedType(typeof(Circle), "circle")]
    public abstract record Shape;

    public sealed record Circle(int Radius) : Shape;
}
