using System.Text.Json.Serialization;
using Amazon.Lambda.APIGatewayEvents;

[JsonSerializable(typeof(UserSignedUp))]
[JsonSerializable(typeof(Welcome))]
internal sealed partial class ShopJson : JsonSerializerContext;

[JsonSerializable(typeof(APIGatewayHttpApiV2ProxyRequest))]
[JsonSerializable(typeof(APIGatewayHttpApiV2ProxyResponse))]
internal sealed partial class LambdaJson : JsonSerializerContext;
