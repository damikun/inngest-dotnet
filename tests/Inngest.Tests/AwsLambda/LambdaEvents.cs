using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Serialization.SystemTextJson;

namespace Inngest.Tests.AwsLambda;

// Events shaped like the ones API Gateway and Lambda function URLs send (the payload samples of the AWS documentation),
// read by the serializer the managed runtime uses, so the handler gets the objects it gets in production: names the
// serializer does not map stay null, and nothing the event leaves out is made up.
internal static class LambdaEvents
{
    public const string ExecuteApiHost = "a1b2c3d4e5.execute-api.eu-west-1.amazonaws.com";
    public const string FunctionUrlHost = "x7k2m9q4w8z1p3v6n5b0c2d4f6h8j1l3.lambda-url.eu-west-1.on.aws";

    public static readonly (string Name, string Value)[] CallQuery = [("fnId", "shop-send-email"), ("stepId", "step")];

    public const string CallBody = """{"ctx":{"attempt":0,"run_id":"01J0000000000000000000000"},"event":{"name":"user/signed.up"},"steps":{},"version":2}""";

    private static readonly DefaultLambdaJsonSerializer Serializer = new();

    // A request of a REST API (payload format 1.0). path is the path inside the API; contextPath is requestContext.path,
    // which API Gateway fills with the stage in front, and which a test event of the console leaves out.
    public static APIGatewayProxyRequest Rest(
        string method = "POST",
        string path = "/api/inngest",
        string? contextPath = "/prod/api/inngest",
        string stage = "prod",
        string? host = ExecuteApiHost,
        string? forwardedProto = "https",
        (string Name, string Value)[]? query = null,
        (string Name, string Value)[]? headers = null,
        bool singleValueOnly = false,
        string? body = null,
        bool isBase64Encoded = false)
    {
        var allHeaders = Headers(host, forwardedProto, headers, lowercase: false);
        var queryParameters = query ?? [];
        var singleQuery = new JsonObject();
        var multiQuery = new JsonObject();
        foreach (var (name, value) in queryParameters)
        {
            // A repeated name keeps its last value in the single-value form and all of them in the multi-value form.
            singleQuery[name] = value;
            (multiQuery[name] ??= new JsonArray()).AsArray().Add(value);
        }

        var singleHeaders = new JsonObject();
        var multiHeaders = new JsonObject();
        foreach (var (name, value) in allHeaders)
        {
            singleHeaders[name] = value;
            (multiHeaders[name] ??= new JsonArray()).AsArray().Add(value);
        }

        var context = new JsonObject
        {
            ["resourceId"] = "q4w8z1",
            ["resourcePath"] = "/{proxy+}",
            ["httpMethod"] = method,
            ["extendedRequestId"] = "Hq9d0j6HIAMF-3w=",
            ["requestTime"] = "30/Oct/2026:12:00:00 +0000",
            ["accountId"] = "123456789012",
            ["protocol"] = "HTTP/1.1",
            ["stage"] = stage,
            ["domainPrefix"] = host?.Split('.')[0],
            ["requestTimeEpoch"] = 1793361600000,
            ["requestId"] = "c6af9ac6-7b61-11e6-9a41-93e8deadbeef",
            ["identity"] = new JsonObject
            {
                ["cognitoIdentityPoolId"] = null,
                ["accountId"] = null,
                ["cognitoIdentityId"] = null,
                ["caller"] = null,
                ["sourceIp"] = "34.201.1.2",
                ["principalOrgId"] = null,
                ["accessKey"] = null,
                ["cognitoAuthenticationType"] = null,
                ["cognitoAuthenticationProvider"] = null,
                ["userArn"] = null,
                ["userAgent"] = "Go-http-client/2.0",
                ["user"] = null,
            },
            ["domainName"] = host,
            ["apiId"] = "a1b2c3d4e5",
        };
        if (contextPath is not null)
        {
            context["path"] = contextPath;
        }

        var root = new JsonObject
        {
            ["resource"] = "/{proxy+}",
            ["path"] = path,
            ["httpMethod"] = method,
            ["headers"] = singleHeaders,
            ["multiValueHeaders"] = singleValueOnly ? null : multiHeaders,
            ["queryStringParameters"] = queryParameters.Length == 0 ? null : singleQuery,
            ["multiValueQueryStringParameters"] = singleValueOnly || queryParameters.Length == 0 ? null : multiQuery,
            ["pathParameters"] = new JsonObject { ["proxy"] = path.TrimStart('/') },
            ["stageVariables"] = null,
            ["requestContext"] = context,
            ["body"] = body,
            ["isBase64Encoded"] = isBase64Encoded,
        };
        return Deserialize<APIGatewayProxyRequest>(root);
    }

    // A request of an HTTP API with the default payload format 2.0. rawPath starts with the stage unless it is
    // $default. Header names are lowercase and repeated values are joined with commas, as API Gateway does.
    public static APIGatewayHttpApiV2ProxyRequest HttpApi(
        string method = "POST",
        string rawPath = "/api/inngest",
        string rawQueryString = "fnId=shop-send-email&stepId=step",
        string stage = "$default",
        string? host = ExecuteApiHost,
        string? forwardedProto = "https",
        (string Name, string Value)[]? headers = null,
        JsonObject? queryStringParameters = null,
        string[]? cookies = null,
        string? body = null,
        bool isBase64Encoded = false,
        string domainName = ExecuteApiHost)
    {
        var allHeaders = Headers(host, forwardedProto, headers, lowercase: true);
        var headerObject = new JsonObject();
        foreach (var (name, value) in allHeaders)
        {
            headerObject[name] = headerObject[name] is { } existing ? $"{existing.GetValue<string>()},{value}" : value;
        }

        var root = new JsonObject
        {
            ["version"] = "2.0",
            ["routeKey"] = "ANY /api/inngest",
            ["rawPath"] = rawPath,
            ["rawQueryString"] = rawQueryString,
            ["cookies"] = cookies is null ? null : new JsonArray([.. cookies.Select(cookie => (JsonNode?)cookie)]),
            ["headers"] = headerObject,
            ["queryStringParameters"] = queryStringParameters,
            ["requestContext"] = new JsonObject
            {
                ["accountId"] = "123456789012",
                ["apiId"] = "a1b2c3d4e5",
                ["domainName"] = domainName,
                ["domainPrefix"] = domainName.Split('.')[0],
                ["http"] = new JsonObject
                {
                    ["method"] = method,
                    ["path"] = rawPath,
                    ["protocol"] = "HTTP/1.1",
                    ["sourceIp"] = "34.201.1.2",
                    ["userAgent"] = "Go-http-client/2.0",
                },
                ["requestId"] = "Hq9d0j6HIAMEJWQ=",
                ["routeKey"] = "ANY /api/inngest",
                ["stage"] = stage,
                ["time"] = "30/Oct/2026:12:00:00 +0000",
                ["timeEpoch"] = 1793361600000,
            },
            ["body"] = body,
            ["isBase64Encoded"] = isBase64Encoded,
        };
        return Deserialize<APIGatewayHttpApiV2ProxyRequest>(root);
    }

    // A request of a function URL, which uses payload format 2.0 with $default as route and stage and no path
    // parameters or stage variables.
    public static APIGatewayHttpApiV2ProxyRequest FunctionUrl(
        string method = "POST",
        string rawPath = "/api/inngest",
        string rawQueryString = "fnId=shop-send-email&stepId=step",
        (string Name, string Value)[]? headers = null,
        string? body = null,
        bool isBase64Encoded = false) =>
        HttpApi(method, rawPath, rawQueryString, "$default", FunctionUrlHost, "https", headers, body: body, isBase64Encoded: isBase64Encoded, domainName: FunctionUrlHost);

    // What the serializer of the managed runtime turns a response into, which is what API Gateway reads.
    public static WireResponse Wire<T>(T response)
    {
        using var stream = new MemoryStream();
        Serializer.Serialize(response, stream);
        using var document = JsonDocument.Parse(stream.ToArray());
        var root = document.RootElement;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("headers", out var headerElement) && headerElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var header in headerElement.EnumerateObject())
            {
                headers[header.Name] = header.Value.GetString()!;
            }
        }

        return new WireResponse(
            root.GetProperty("statusCode").GetInt32(),
            headers,
            root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString()! : string.Empty,
            root.GetProperty("isBase64Encoded").GetBoolean(),
            root.Clone());
    }

    public static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    // The headers a caller of the app sends, as API Gateway passes them on.
    private static List<(string Name, string Value)> Headers(string? host, string? forwardedProto, (string Name, string Value)[]? extra, bool lowercase)
    {
        List<(string Name, string Value)> headers =
        [
            ("Content-Type", "application/json"),
            ("User-Agent", "Go-http-client/2.0"),
            ("X-Amzn-Trace-Id", "Root=1-67891233-abcdef012345678912345678"),
            ("X-Forwarded-For", "34.201.1.2"),
            ("X-Forwarded-Port", "443"),
        ];
        if (host is not null)
        {
            headers.Add(("Host", host));
        }

        if (forwardedProto is not null)
        {
            headers.Add(("X-Forwarded-Proto", forwardedProto));
        }

        headers.AddRange(extra ?? []);
        return lowercase ? [.. headers.Select(header => (header.Name.ToLowerInvariant(), header.Value))] : headers;
    }

    private static T Deserialize<T>(JsonObject root)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(root.ToJsonString()));
        return Serializer.Deserialize<T>(stream);
    }
}

// A response as it leaves the function: the JSON the serializer writes, read back.
internal sealed record WireResponse(int StatusCode, IReadOnlyDictionary<string, string> Headers, string Body, bool IsBase64Encoded, JsonElement Json)
{
    // The bytes API Gateway sends on as the body.
    public byte[] BodyBytes => IsBase64Encoded ? Convert.FromBase64String(Body) : Encoding.UTF8.GetBytes(Body);

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}
