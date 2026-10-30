using System.Collections.Concurrent;
using System.Text;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Inngest.IntegrationTests.AwsLambda;

public enum GatewayPayload
{
    HttpApi,
    RestApi,
}

// What the fake gateway turns requests into, and what it has turned so far. A test that needs another kind of event
// switches it and puts it back.
public sealed class GatewaySettings
{
    public GatewayPayload Payload { get; set; } = GatewayPayload.HttpApi;

    // Bodies as base64 text, which API Gateway sends when a binary media type such as */* is configured.
    public bool Base64Bodies { get; set; }

    public ConcurrentQueue<GatewayCall> Calls { get; } = new();
}

public sealed record GatewayCall(GatewayPayload Payload, string Method, string Path, bool IsBase64Encoded);

// API Gateway in front of a Lambda function, for a test: it runs ahead of the SDK's own endpoint and answers every
// request to the serve path itself. Each request becomes an event of the chosen payload format, with the fields API
// Gateway fills in, goes to the Lambda handler, and the response comes back out as HTTP. The app listens on plain
// http, so the events say so in X-Forwarded-Proto, as an emulator's would.
internal sealed class FakeApiGateway(InngestLambdaHandler handler, GatewaySettings settings) : IStartupFilter
{
    // The first segment of the serve path is the stage of a REST API, so that its events show the stage in
    // requestContext.path and not in path.
    private const string Stage = "api";

    private static readonly TestLambdaContext Context = new() { RemainingTime = TimeSpan.FromMinutes(5) };

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            if (!context.Request.Path.StartsWithSegments(TestPaths.Serve))
            {
                await nextMiddleware(context);
                return;
            }

            await InvokeAsync(context);
        });
        next(app);
    };

    private async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, context.RequestAborted);
        var bytes = buffer.ToArray();
        var base64 = settings.Base64Bodies && bytes.Length > 0;
        var body = bytes.Length == 0 ? null : base64 ? Convert.ToBase64String(bytes) : Encoding.UTF8.GetString(bytes);
        settings.Calls.Enqueue(new GatewayCall(settings.Payload, request.Method, request.Path.Value!, base64));

        int status;
        IDictionary<string, string>? headers;
        string? responseBody;
        bool responseIsBase64;
        try
        {
            if (settings.Payload == GatewayPayload.RestApi)
            {
                var response = await handler.HandleAsync(RestEvent(request, body, base64), Context);
                (status, headers, responseBody, responseIsBase64) = (response.StatusCode, response.Headers, response.Body, response.IsBase64Encoded);
            }
            else
            {
                var response = await handler.HandleAsync(HttpApiEvent(request, body, base64), Context);
                (status, headers, responseBody, responseIsBase64) = (response.StatusCode, response.Headers, response.Body, response.IsBase64Encoded);
            }
        }
        catch (Exception)
        {
            // What API Gateway answers when the function fails.
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsync("""{"message": "Internal server error"}""", context.RequestAborted);
            return;
        }

        context.Response.StatusCode = status;
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            context.Response.Headers[name] = value;
        }

        var payload = responseIsBase64 ? Convert.FromBase64String(responseBody ?? string.Empty) : Encoding.UTF8.GetBytes(responseBody ?? string.Empty);
        context.Response.ContentLength = payload.Length;
        await context.Response.Body.WriteAsync(payload, context.RequestAborted);
    }

    // Payload format 2.0: lowercase header names with repeated values joined by commas, the raw path and query string,
    // and parameters that API Gateway has decoded.
    private static APIGatewayHttpApiV2ProxyRequest HttpApiEvent(HttpRequest request, string? body, bool base64)
    {
        var headers = new Dictionary<string, string>();
        foreach (var (name, values) in request.Headers)
        {
            headers[name.ToLowerInvariant()] = string.Join(',', values.ToArray());
        }

        headers["x-forwarded-proto"] = "http";
        var query = new Dictionary<string, string>();
        foreach (var (name, values) in request.Query)
        {
            query[name] = string.Join(',', values.ToArray());
        }

        var path = request.Path.Value!;
        return new APIGatewayHttpApiV2ProxyRequest
        {
            Version = "2.0",
            RouteKey = "$default",
            RawPath = path,
            RawQueryString = request.QueryString.Value?.TrimStart('?') ?? string.Empty,
            Headers = headers,
            QueryStringParameters = query.Count == 0 ? null : query,
            Body = body,
            IsBase64Encoded = base64,
            RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
            {
                ApiId = "itests",
                DomainName = request.Host.Host,
                Stage = "$default",
                RouteKey = "$default",
                RequestId = Guid.NewGuid().ToString(),
                Http = new APIGatewayHttpApiV2ProxyRequest.HttpDescription
                {
                    Method = request.Method,
                    Path = path,
                    Protocol = "HTTP/1.1",
                    SourceIp = "127.0.0.1",
                },
            },
        };
    }

    // Payload format 1.0: header names as sent, in a single-value and a multi-value form, parameters in both forms,
    // the path inside the API, and the whole path with the stage in front of it in the request context.
    private static APIGatewayProxyRequest RestEvent(HttpRequest request, string? body, bool base64)
    {
        var fullPath = request.Path.Value!;
        var inner = fullPath.StartsWith($"/{Stage}", StringComparison.Ordinal) ? fullPath[(Stage.Length + 1)..] : fullPath;
        inner = inner.Length == 0 ? "/" : inner;

        var headers = new Dictionary<string, string>();
        var multiValueHeaders = new Dictionary<string, IList<string>>();
        foreach (var (name, values) in request.Headers)
        {
            headers[name] = values.Last()!;
            multiValueHeaders[name] = [.. values.Select(value => value!)];
        }

        headers["X-Forwarded-Proto"] = "http";
        multiValueHeaders["X-Forwarded-Proto"] = ["http"];
        var query = new Dictionary<string, string>();
        var multiValueQuery = new Dictionary<string, IList<string>>();
        foreach (var (name, values) in request.Query)
        {
            query[name] = values.Last()!;
            multiValueQuery[name] = [.. values.Select(value => value!)];
        }

        return new APIGatewayProxyRequest
        {
            Resource = "/{proxy+}",
            Path = inner,
            HttpMethod = request.Method,
            Headers = headers,
            MultiValueHeaders = multiValueHeaders,
            QueryStringParameters = query.Count == 0 ? null : query,
            MultiValueQueryStringParameters = multiValueQuery.Count == 0 ? null : multiValueQuery,
            PathParameters = new Dictionary<string, string> { ["proxy"] = inner.TrimStart('/') },
            Body = body,
            IsBase64Encoded = base64,
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                ApiId = "itests",
                DomainName = request.Host.Host,
                Stage = Stage,
                Path = fullPath,
                ResourcePath = "/{proxy+}",
                HttpMethod = request.Method,
                RequestId = Guid.NewGuid().ToString(),
                Identity = new APIGatewayProxyRequest.RequestIdentity { SourceIp = "127.0.0.1" },
            },
        };
    }
}

internal static class TestPaths
{
    public const string Serve = "/api/inngest";
}
