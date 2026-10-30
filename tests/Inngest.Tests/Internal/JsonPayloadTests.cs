using System.Text.Json;
using System.Text.Json.Nodes;
using Inngest.Json;

namespace Inngest.Tests.Internal;

public sealed class JsonPayloadTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() };

    public static TheoryData<object> Objects =>
    [
        new { orderId = 1 },
        new Dictionary<string, int> { ["a"] = 1 },
        JsonDocument.Parse("""{"a":1}""").RootElement,
        new JsonObject { ["a"] = 1 },
    ];

    public static TheoryData<object> NonObjects =>
    [
        42,
        "text",
        true,
        Guid.Empty,
        new[] { 1, 2 },
        new List<string> { "a" },
        JsonDocument.Parse("[1]").RootElement,
        new JsonArray(1),
    ];

    [Theory]
    [MemberData(nameof(Objects))]
    public void Accepts_values_that_serialize_to_objects(object value) =>
        Should.NotThrow(() => JsonPayload.EnsureObject(value, Options, "data", "event", "shop/order"));

    [Theory]
    [MemberData(nameof(NonObjects))]
    public void Rejects_values_that_do_not(object value) =>
        Should.Throw<ArgumentException>(() => JsonPayload.EnsureObject(value, Options, "data", "event", "shop/order")).Message.ShouldStartWith("The data of event \"shop/order\" must serialize to a JSON object");

    [Fact]
    public void Null_means_no_data() => Should.NotThrow(() => JsonPayload.EnsureObject(null, Options, "data", "event", "shop/order"));
}
