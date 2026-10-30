// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text;
using Amazon.Lambda.APIGatewayEvents;
using Inngest.Serving;

namespace Inngest.AwsLambda;

// A ServeResponse as the response shapes of API Gateway. The body is JSON, so it travels as UTF-8 text and the
// signature of the response, which is computed over its bytes, still matches what API Gateway sends on.
internal static class LambdaResponses
{
    public static APIGatewayProxyResponse ToRestApi(ServeResponse response) => new()
    {
        StatusCode = response.StatusCode,
        Headers = Headers(response),
        Body = Body(response),
    };

    public static APIGatewayHttpApiV2ProxyResponse ToHttpApi(ServeResponse response) => new()
    {
        StatusCode = response.StatusCode,
        Headers = Headers(response),
        Body = Body(response),
    };

    // The handler names each header once, so the single-value form of both shapes is enough.
    private static Dictionary<string, string> Headers(ServeResponse response)
    {
        var headers = new Dictionary<string, string>(response.Headers.Count);
        foreach (var (name, value) in response.Headers)
        {
            headers[name] = value;
        }

        return headers;
    }

    private static string Body(ServeResponse response) =>
        response.Body.IsEmpty ? string.Empty : Encoding.UTF8.GetString(response.Body.Span);
}
