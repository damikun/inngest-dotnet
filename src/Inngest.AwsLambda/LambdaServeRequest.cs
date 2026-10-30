// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Amazon.Lambda.APIGatewayEvents;
using Inngest.Serving;

namespace Inngest.AwsLambda;

// An event of API Gateway or a function URL as the serve handler sees a request. Both payload formats are reduced to
// the same few fields when the request is created.
internal sealed class LambdaServeRequest : IServeRequest
{
    private const string HostHeader = "Host";
    private const string ForwardedProtoHeader = "X-Forwarded-Proto";

    private readonly Dictionary<string, string> _headers;
    private readonly string _path;
    private readonly string _query;
    private readonly string? _domainName;
    private Uri? _url;

    private LambdaServeRequest(
        string method,
        string path,
        string query,
        Dictionary<string, string> headers,
        string? domainName,
        ReadOnlyMemory<byte> body)
    {
        Method = method;
        Body = body;
        _path = path;
        _query = query;
        _headers = headers;
        _domainName = domainName;
    }

    public string Method { get; }

    // Built on first use: only syncs need it.
    public Uri Url => _url ??= CreateUrl();

    public ReadOnlyMemory<byte> Body { get; }

    // The name inngest-js reports for its Lambda adapter.
    public string Framework => "aws-lambda";

    // Without a scope the handler creates one per call, which is what an invocation is.
    public IServiceProvider? Services => null;

    public string? GetHeader(string name) => _headers.TryGetValue(name, out var value) ? value : null;

    public string? GetQuery(string name) => LambdaQuery.Find(_query, name);

    // REST APIs, and HTTP APIs that use payload format 1.0.
    public static LambdaServeRequest Create(APIGatewayProxyRequest request, ReadOnlyMemory<byte> body)
    {
        var method = request.HttpMethod ?? request.RequestContext?.HttpMethod;
        if (string.IsNullOrEmpty(method))
        {
            throw new ArgumentException(
                "The event has no httpMethod. This overload is for API Gateway REST APIs, which send payload format 1.0; HTTP APIs and function URLs send payload format 2.0.",
                nameof(request));
        }

        return new LambdaServeRequest(
            method,
            RestPath(request),
            LambdaQuery.Build(request.MultiValueQueryStringParameters, request.QueryStringParameters),
            Headers(request.Headers, request.MultiValueHeaders),
            request.RequestContext?.DomainName,
            body);
    }

    // HTTP APIs and function URLs, which use payload format 2.0.
    public static LambdaServeRequest Create(APIGatewayHttpApiV2ProxyRequest request, ReadOnlyMemory<byte> body)
    {
        var http = request.RequestContext?.Http;
        var method = http?.Method;
        if (string.IsNullOrEmpty(method))
        {
            throw new ArgumentException(
                "The event has no requestContext.http.method. This overload is for HTTP APIs and function URLs, which send payload format 2.0; REST APIs send payload format 1.0.",
                nameof(request));
        }

        // The raw string keeps what the client sent. queryStringParameters is not a reliable source: HTTP APIs decode
        // it twice, so a %2B arrives as a space. It is only used when the event has no raw string at all.
        var query = request.RawQueryString is { } raw
            ? raw.TrimStart('?')
            : LambdaQuery.Build(multiValues: null, request.QueryStringParameters);

        // The path of an HTTP API starts with the stage unless the stage is $default, so it is already the public path.
        return new LambdaServeRequest(
            method,
            Normalize(string.IsNullOrEmpty(request.RawPath) ? http!.Path : request.RawPath),
            query,
            Headers(request.Headers, multiValues: null),
            request.RequestContext!.DomainName,
            body);
    }

    // requestContext.path is the path the client called, which starts with the stage on the execute-api domain, while
    // path is the path inside the API. Where the event holds anything else there, such as a console test event without
    // it or a payload format 1.0 event of an HTTP API that has the stage in path, path stays in charge.
    private static string RestPath(APIGatewayProxyRequest request)
    {
        var path = Normalize(request.Path);
        if (request.RequestContext?.Path is not { Length: > 0 } fullPath)
        {
            return path;
        }

        var full = Normalize(fullPath);
        return full.TrimEnd('/').EndsWith(path.TrimEnd('/'), StringComparison.Ordinal) ? full : path;
    }

    private static string Normalize(string? path) =>
        string.IsNullOrEmpty(path) ? "/" : path[0] == '/' ? path : "/" + path;

    // Header names are matched without regard to case: REST APIs keep the case the client sent, HTTP APIs and function
    // URLs lowercase every name, and test events and emulators do either. The multi-value form of a REST API holds
    // every header, so it wins over the single-value one.
    private static Dictionary<string, string> Headers(IDictionary<string, string>? values, IDictionary<string, IList<string>>? multiValues)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (values is not null)
        {
            foreach (var (name, value) in values)
            {
                if (value is not null)
                {
                    headers[name.Trim()] = value;
                }
            }
        }

        if (multiValues is not null)
        {
            foreach (var (name, list) in multiValues)
            {
                if (list is { Count: > 0 })
                {
                    headers[name.Trim()] = string.Join(',', list);
                }
            }
        }

        return headers;
    }

    // The URL Inngest called: the scheme the caller used, the host it asked for and the public path and query. The
    // stage and base path mappings are in the path as far as the event shows them; a custom domain that hides them, or
    // a CloudFront distribution that changes the host, needs InngestOptions.ServeOrigin and ServePath.
    private Uri CreateUrl()
    {
        var host = GetHeader(HostHeader) is { Length: > 0 } header ? header : _domainName;
        if (string.IsNullOrEmpty(host))
        {
            throw new InvalidOperationException(
                "The event has neither a Host header nor a domain name in its request context, so the URL of the app is unknown. Set InngestOptions.ServeOrigin.");
        }

        var url = $"{Scheme()}://{host}{_path}{(_query.Length == 0 ? string.Empty : "?" + _query)}";
        return Uri.TryCreate(url, UriKind.Absolute, out var result)
            ? result
            : throw new InvalidOperationException($"The event describes \"{url}\", which is not a valid URL. Set InngestOptions.ServeOrigin and ServePath.");
    }

    // API Gateway and function URLs always call https, but an emulator may use http. After proxies the header lists
    // every hop, and the closest proxy appends the last entry: that is the one ASP.NET Core's forwarded headers
    // middleware trusts, since a caller can put anything in front of it.
    private string Scheme()
    {
        if (GetHeader(ForwardedProtoHeader) is { } forwarded)
        {
            var last = forwarded.AsSpan();
            var comma = last.LastIndexOf(',');
            last = (comma < 0 ? last : last[(comma + 1)..]).Trim();
            if (last.Equals("http", StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UriSchemeHttp;
            }
        }

        return Uri.UriSchemeHttps;
    }
}
