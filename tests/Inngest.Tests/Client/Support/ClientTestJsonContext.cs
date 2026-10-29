using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inngest.Tests.Client;

internal sealed record OrderPlaced(string OrderId, int Quantity);

// What an app that runs without reflection configures: its own types and nothing else.
[JsonSerializable(typeof(OrderPlaced))]
internal sealed partial class ClientTestJsonContext : JsonSerializerContext
{
}

// An app that already covers the types the SDK needs.
[JsonSerializable(typeof(OrderPlaced))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(FunctionFailure))]
[JsonSerializable(typeof(FunctionError))]
[JsonSerializable(typeof(FunctionFinished))]
[JsonSerializable(typeof(FunctionCancelled))]
[JsonSerializable(typeof(SendEventsResult))]
internal sealed partial class CompleteTestJsonContext : JsonSerializerContext
{
}
