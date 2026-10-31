namespace Inngest.SourceGeneration.Tests.Support;

// Types for every rule by which System.Text.Json reaches a type, behind one root that a context lists. Each type is only
// reachable one way, and its name says which, so the tests below can ask whether it is covered. Which are covered was checked
// against the source generator of System.Text.Json in .NET 8 and .NET 10, which agree; AgreementTests asks it again whenever
// it can find one.
internal static class ProbeTypes
{
    // The types that must be covered (true) and those that must not (false). Types of the root itself and the helpers are
    // not listed.
    public static readonly IReadOnlyDictionary<string, bool> Expected = new Dictionary<string, bool>
    {
        // Members
        ["ViaPublicProperty"] = true,
        ["ViaGetOnlyProperty"] = true,
        ["ViaSetOnlyProperty"] = true,
        ["ViaInternalProperty"] = false,
        ["ViaPrivateProperty"] = false,
        ["ViaProtectedProperty"] = false,
        ["ViaProtectedInternalProperty"] = false,
        ["ViaPrivateProtectedProperty"] = false,
        ["ViaIncludedInternalProperty"] = true,
        ["ViaStaticProperty"] = false,
        ["ViaExplicitInterfaceProperty"] = false,
        ["ViaIgnoredProperty"] = false,
        ["ViaConditionallyIgnoredProperty"] = true,
        ["ViaNeverIgnoredProperty"] = true,
        ["ViaBaseClassProperty"] = true,
        ["ViaPublicField"] = true,
        ["ViaInternalField"] = false,
        ["ViaPrivateField"] = false,
        ["ViaIncludedInternalField"] = true,
        ["ViaStaticField"] = false,
        ["ViaIgnoredField"] = false,

        // Constructors: the one the generator calls
        ["ViaOnlyConstructor"] = true,
        ["ViaOnlyConstructorOfIgnoredProperty"] = true,
        ["ViaUnusedConstructor"] = false,
        ["ViaAmbiguousConstructorA"] = false,
        ["ViaAmbiguousConstructorB"] = false,
        ["ViaMarkedConstructor"] = true,
        ["ViaUnmarkedConstructor"] = false,
        ["ViaInternalConstructor"] = false,
        ["ViaMarkedInternalConstructor"] = true,
        ["ViaRecordParameter"] = true,

        // Collections, which are written as their elements
        ["ViaListSubclassElement"] = true,
        ["ViaListSubclassProperty"] = false,
        ["ViaListSubclassConstructor"] = false,
        ["ViaEnumerableElement"] = true,
        ["ViaDictionarySubclassValue"] = true,
        ["ViaDictionaryKey"] = true,
        ["ViaDictionaryValue"] = true,
        ["ViaSetElement"] = true,
        ["ViaTupleArgument"] = true,
        ["ViaArrayElement"] = true,

        // Other types
        ["ViaConvertedTypeMember"] = false,
        ["ViaConvertedTypeConstructor"] = false,
        ["ViaPropertyWithConverter"] = true,
        ["ViaDerivedTypeAttribute"] = true,
        ["ViaDerivedTypeMember"] = true,
        ["ViaInterfaceMember"] = true,
        ["ViaInterfaceBaseMember"] = true,
        ["ViaGenericArgumentMember"] = true,
        ["ViaNullableStruct"] = true,
        ["ViaStructProperty"] = true,
        ["ViaStructConstructor"] = false,
        ["ViaObsoleteProperty"] = true,
        ["NeverReached"] = false,
    };

    public const string Source = """
        using System;
        using System.Collections;
        using System.Collections.Generic;
        using System.Text.Json;
        using System.Text.Json.Serialization;

        namespace Probe;

        public sealed class Root
        {
            public string Id { get; set; } = "";

            public PublicHolder Public { get; set; } = null!;
            public ConstructorHolder Constructors { get; set; } = null!;
            public CollectionHolder Collections { get; set; } = null!;
            public OtherHolder Other { get; set; } = null!;
        }

        // Members
        public sealed class ViaPublicProperty { public int V { get; set; } }
        public sealed class ViaGetOnlyProperty { public int V { get; set; } }
        public sealed class ViaSetOnlyProperty { public int V { get; set; } }
        public sealed class ViaInternalProperty { public int V { get; set; } }
        public sealed class ViaPrivateProperty { public int V { get; set; } }
        public sealed class ViaProtectedProperty { public int V { get; set; } }
        public sealed class ViaProtectedInternalProperty { public int V { get; set; } }
        public sealed class ViaPrivateProtectedProperty { public int V { get; set; } }
        public sealed class ViaIncludedInternalProperty { public int V { get; set; } }
        public sealed class ViaStaticProperty { public int V { get; set; } }
        public sealed class ViaExplicitInterfaceProperty { public int V { get; set; } }
        public sealed class ViaIgnoredProperty { public int V { get; set; } }
        public sealed class ViaConditionallyIgnoredProperty { public int V { get; set; } }
        public sealed class ViaNeverIgnoredProperty { public int V { get; set; } }
        public sealed class ViaBaseClassProperty { public int V { get; set; } }
        public sealed class ViaPublicField { public int V { get; set; } }
        public sealed class ViaInternalField { public int V { get; set; } }
        public sealed class ViaPrivateField { public int V { get; set; } }
        public sealed class ViaIncludedInternalField { public int V { get; set; } }
        public sealed class ViaStaticField { public int V { get; set; } }
        public sealed class ViaIgnoredField { public int V { get; set; } }

        public interface IExplicit { ViaExplicitInterfaceProperty Explicit { get; } }
        public class HolderBase { public ViaBaseClassProperty FromBase { get; set; } = null!; }

        public class PublicHolder : HolderBase, IExplicit
        {
            public ViaPublicProperty A { get; set; } = null!;
            public ViaGetOnlyProperty B { get; } = null!;
            public ViaSetOnlyProperty C { set { } }
            internal ViaInternalProperty D { get; set; } = null!;
            private ViaPrivateProperty E { get; set; } = null!;
            protected ViaProtectedProperty F { get; set; } = null!;
            protected internal ViaProtectedInternalProperty G { get; set; } = null!;
            private protected ViaPrivateProtectedProperty H { get; set; } = null!;
            [JsonInclude] internal ViaIncludedInternalProperty I { get; set; } = null!;
            public static ViaStaticProperty J { get; set; } = null!;
            ViaExplicitInterfaceProperty IExplicit.Explicit => null!;
            [JsonIgnore] public ViaIgnoredProperty K { get; set; } = null!;
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ViaConditionallyIgnoredProperty L { get; set; } = null!;
            [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public ViaNeverIgnoredProperty M { get; set; } = null!;
            public ViaPublicField N = null!;
            internal ViaInternalField O = null!;
            private ViaPrivateField P = null!;
            [JsonInclude] internal ViaIncludedInternalField Q = null!;
            public static ViaStaticField R = null!;
            [JsonIgnore] public ViaIgnoredField S = null!;
            [Obsolete("x")] public ViaObsoleteProperty T { get; set; } = null!;
        }

        public sealed class ViaObsoleteProperty { public int V { get; set; } }

        // Constructors
        public sealed class ViaOnlyConstructor { public int V { get; set; } }
        public sealed class ViaOnlyConstructorOfIgnoredProperty { public int V { get; set; } }
        public sealed class ViaUnusedConstructor { public int V { get; set; } }
        public sealed class ViaAmbiguousConstructorA { public int V { get; set; } }
        public sealed class ViaAmbiguousConstructorB { public int V { get; set; } }
        public sealed class ViaMarkedConstructor { public int V { get; set; } }
        public sealed class ViaUnmarkedConstructor { public int V { get; set; } }
        public sealed class ViaInternalConstructor { public int V { get; set; } }
        public sealed class ViaMarkedInternalConstructor { public int V { get; set; } }
        public sealed class ViaRecordParameter { public int V { get; set; } }
        public sealed class ViaStructProperty { public int V { get; set; } }
        public sealed class ViaStructConstructor { public int V { get; set; } }

        public sealed class OnlyConstructor { public OnlyConstructor(ViaOnlyConstructor parameter) { } public int V { get; set; } }
        public sealed class IgnoredPropertyConstructor
        {
            public IgnoredPropertyConstructor(ViaOnlyConstructorOfIgnoredProperty parameter) { Parameter = parameter; }

            [JsonIgnore] public ViaOnlyConstructorOfIgnoredProperty Parameter { get; }
        }

        public sealed class UnusedConstructor { public UnusedConstructor() { } public UnusedConstructor(ViaUnusedConstructor parameter) { } public int V { get; set; } }
        public sealed class AmbiguousConstructors { public AmbiguousConstructors(ViaAmbiguousConstructorA a) { } public AmbiguousConstructors(ViaAmbiguousConstructorB b) { } public int V { get; set; } }
        public sealed class MarkedConstructor { [JsonConstructor] public MarkedConstructor(ViaMarkedConstructor a) { } public MarkedConstructor(ViaUnmarkedConstructor b) { } public int V { get; set; } }
        public sealed class InternalConstructor { internal InternalConstructor(ViaInternalConstructor parameter) { } public InternalConstructor() { } public int V { get; set; } }
        public sealed class MarkedInternalConstructor { [JsonConstructor] internal MarkedInternalConstructor(ViaMarkedInternalConstructor parameter) { } public int V { get; set; } }
        public sealed record RecordWithParameter(ViaRecordParameter Parameter);
        public struct StructWithConstructor { public StructWithConstructor(ViaStructConstructor parameter) { Property = null!; } public ViaStructProperty Property { get; } }

        public sealed class ConstructorHolder
        {
            public OnlyConstructor A { get; set; } = null!;
            public IgnoredPropertyConstructor B { get; set; } = null!;
            public UnusedConstructor C { get; set; } = null!;
            public AmbiguousConstructors D { get; set; } = null!;
            public MarkedConstructor E { get; set; } = null!;
            public InternalConstructor F { get; set; } = null!;
            public MarkedInternalConstructor G { get; set; } = null!;
            public RecordWithParameter H { get; set; } = null!;
            public StructWithConstructor I { get; set; }
        }

        // Collections
        public sealed class ViaListSubclassElement { public int V { get; set; } }
        public sealed class ViaListSubclassProperty { public int V { get; set; } }
        public sealed class ViaListSubclassConstructor { public int V { get; set; } }
        public sealed class ViaEnumerableElement { public int V { get; set; } }
        public sealed class ViaDictionarySubclassValue { public int V { get; set; } }
        public sealed class ViaDictionaryKey { public int V { get; set; } }
        public sealed class ViaDictionaryValue { public int V { get; set; } }
        public sealed class ViaSetElement { public int V { get; set; } }
        public sealed class ViaTupleArgument { public int V { get; set; } }
        public sealed class ViaArrayElement { public int V { get; set; } }

        public sealed class ListSubclass : List<ViaListSubclassElement>
        {
            public ListSubclass() { }
            public ListSubclass(ViaListSubclassConstructor parameter) { }

            public ViaListSubclassProperty Extra { get; set; } = null!;
        }

        public sealed class EnumerableImplementation : IEnumerable<ViaEnumerableElement>
        {
            public IEnumerator<ViaEnumerableElement> GetEnumerator() => throw new NotImplementedException();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        public sealed class DictionarySubclass : Dictionary<string, ViaDictionarySubclassValue> { }

        public sealed class CollectionHolder
        {
            public ListSubclass A { get; set; } = null!;
            public EnumerableImplementation B { get; set; } = null!;
            public DictionarySubclass C { get; set; } = null!;
            public Dictionary<ViaDictionaryKey, ViaDictionaryValue> D { get; set; } = null!;
            public HashSet<ViaSetElement> E { get; set; } = null!;
            public Tuple<ViaTupleArgument, int> F { get; set; } = null!;
            public ViaArrayElement[] G { get; set; } = null!;
        }

        // Other types
        public sealed class ViaConvertedTypeMember { public int V { get; set; } }
        public sealed class ViaConvertedTypeConstructor { public int V { get; set; } }
        public sealed class ViaPropertyWithConverter { public int V { get; set; } }
        public sealed class ViaDerivedTypeAttribute { public int V { get; set; } }
        public sealed class ViaDerivedTypeMember { public int V { get; set; } }
        public sealed class ViaInterfaceMember { public int V { get; set; } }
        public sealed class ViaInterfaceBaseMember { public int V { get; set; } }
        public sealed class ViaGenericArgumentMember { public int V { get; set; } }
        public struct ViaNullableStruct { public int V { get; set; } }
        public sealed class NeverReached { public int V { get; set; } }

        [JsonConverter(typeof(ConvertedConverter))]
        public sealed class Converted
        {
            public Converted(ViaConvertedTypeConstructor parameter) { }

            public ViaConvertedTypeMember Member { get; set; } = null!;
        }

        public sealed class ConvertedConverter : JsonConverter<Converted>
        {
            public override Converted? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => null;
            public override void Write(Utf8JsonWriter writer, Converted value, JsonSerializerOptions options) { }
        }

        public sealed class PropertyConverter : JsonConverter<ViaPropertyWithConverter>
        {
            public override ViaPropertyWithConverter? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => null;
            public override void Write(Utf8JsonWriter writer, ViaPropertyWithConverter value, JsonSerializerOptions options) { }
        }

        [JsonDerivedType(typeof(Derived))]
        public abstract class Shape { public int Id { get; set; } }
        public sealed class Derived : Shape { public ViaDerivedTypeMember Member { get; set; } = null!; }

        public interface IThingBase { ViaInterfaceBaseMember BaseMember { get; } }
        public interface IThing : IThingBase { ViaInterfaceMember Member { get; } }
        public sealed class Wrapper<T> { public T Value { get; set; } = default!; }

        [JsonDerivedType(typeof(ViaDerivedTypeAttribute))]
        public interface IPolymorphic { }

        public sealed class OtherHolder
        {
            public Converted A { get; set; } = null!;
            [JsonConverter(typeof(PropertyConverter))] public ViaPropertyWithConverter B { get; set; } = null!;
            public Shape C { get; set; } = null!;
            public IThing D { get; set; } = null!;
            public Wrapper<ViaGenericArgumentMember> E { get; set; } = null!;
            public ViaNullableStruct? F { get; set; }
            public IPolymorphic G { get; set; } = null!;
        }

        [JsonSerializable(typeof(Root))]
        public sealed partial class Ctx : JsonSerializerContext;
        """;
}
