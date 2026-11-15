using System.Collections;
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

    // "kind" is the discriminator of Part, and an ordinary property of Ring, which is read by "type".
    [Fact]
    public void Each_object_moves_only_the_discriminator_of_the_type_it_is_read_as()
    {
        const string Sorted = """{"figure":{"kind":"x","size":1,"type":"ring"},"part":{"bark":"b","kind":"twig"}}""";

        var moved = MoveFirst<Drawing>(Sorted);

        moved.GetRawText().ShouldBe("""{"figure":{"type":"ring","kind":"x","size":1},"part":{"kind":"twig","bark":"b"}}""");
        var drawing = moved.Deserialize<Drawing>(Options)!;
        drawing.Figure.ShouldBe(new Ring("x", 1));
        drawing.Part.ShouldBe(new Twig("b"));
    }

    [Fact]
    public void The_members_of_a_derived_type_are_read_by_that_type()
    {
        const string Sorted = """{"body":{"bark":"b","kind":"twig"},"v":1}""";

        var moved = MoveFirst<Versioned>(Sorted);

        moved.GetRawText().ShouldBe("""{"v":1,"body":{"kind":"twig","bark":"b"}}""");
        moved.Deserialize<Versioned>(Options).ShouldBe(new VersionOne(new Twig("b")));
    }

    [Fact]
    public void A_collection_class_that_is_not_generic_is_looked_into()
    {
        const string Sorted = """{"methods":[{"iban":"DE89","type":"bank"}]}""";

        var moved = MoveFirst<Purse>(Sorted);

        moved.GetRawText().ShouldBe("""{"methods":[{"type":"bank","iban":"DE89"}]}""");
        moved.Deserialize<Purse>(Options)!.Methods.ShouldHaveSingleItem().ShouldBe(new BankTransfer("DE89"));
    }

    // System.Text.Json writes a polymorphic collection as an object with its items under "$values".
    [Fact]
    public void The_items_of_a_polymorphic_collection_are_looked_into()
    {
        const string Sorted = """{"$type":"history","$values":[{"iban":"DE89","type":"bank"}]}""";

        var moved = MoveFirst<MethodLog>(Sorted);

        moved.GetRawText().ShouldBe("""{"$type":"history","$values":[{"type":"bank","iban":"DE89"}]}""");
        moved.Deserialize<MethodLog>(Options).ShouldBeOfType<MethodHistory>().ShouldHaveSingleItem().ShouldBe(new BankTransfer("DE89"));
    }

    [Fact]
    public void A_polymorphic_collection_with_a_custom_discriminator_has_it_moved_before_its_items()
    {
        const string Sorted = """{"$values":[{"iban":"DE89","type":"bank"}],"source":"import"}""";

        var moved = MoveFirst<MethodBatch>(Sorted);

        moved.GetRawText().ShouldBe("""{"source":"import","$values":[{"type":"bank","iban":"DE89"}]}""");
        moved.Deserialize<MethodBatch>(Options).ShouldBeOfType<ImportedBatch>().ShouldHaveSingleItem().ShouldBe(new BankTransfer("DE89"));
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
    [JsonDerivedType(typeof(Twig), "twig")]
    public abstract record Part;

    public sealed record Leaf(string Name) : Part;

    public sealed record Twig(string Bark) : Part;

    public sealed record Wallet(string Id, List<PaymentMethod> Methods, Dictionary<string, PaymentMethod> ByName, Node Nested);

    public sealed record Invoice(string Type, int Total);

    [JsonPolymorphic]
    [JsonDerivedType(typeof(Circle), "circle")]
    public abstract record Shape;

    public sealed record Circle(int Radius) : Shape;

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
    [JsonDerivedType(typeof(Ring), "ring")]
    public abstract record Figure;

    public sealed record Ring(string Kind, int Size) : Figure;

    public sealed record Drawing(Figure Figure, Part Part);

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "v")]
    [JsonDerivedType(typeof(VersionOne), 1)]
    public abstract record Versioned;

    public sealed record VersionOne(Part Body) : Versioned;

    public sealed record Purse(MethodList Methods);

    [JsonPolymorphic]
    [JsonDerivedType(typeof(MethodHistory), "history")]
    public class MethodLog : List<PaymentMethod>;

    public sealed class MethodHistory : MethodLog;

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "source")]
    [JsonDerivedType(typeof(ImportedBatch), "import")]
    public class MethodBatch : List<PaymentMethod>;

    public sealed class ImportedBatch : MethodBatch;

    public sealed class MethodList : ICollection<PaymentMethod>
    {
        private readonly List<PaymentMethod> _items = [];

        public int Count => _items.Count;

        public bool IsReadOnly => false;

        public void Add(PaymentMethod item) => _items.Add(item);

        public void Clear() => _items.Clear();

        public bool Contains(PaymentMethod item) => _items.Contains(item);

        public void CopyTo(PaymentMethod[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);

        public bool Remove(PaymentMethod item) => _items.Remove(item);

        public IEnumerator<PaymentMethod> GetEnumerator() => _items.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
