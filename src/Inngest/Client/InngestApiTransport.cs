// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Net;
using System.Net.Http.Headers;
using Inngest.Configuration;
using Inngest.Internal;
using Inngest.Signing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Inngest.Client;

// Sends requests to the Inngest REST API with the SDK headers and the hashed signing key as a bearer token. A
// request Inngest rejects with 401 or 403 is sent once more with the fallback key, which is what a key rotation in
// progress looks like. Each caller passes its own logger, so the fallback shows up under the caller's category.
internal sealed class InngestApiTransport(IHttpClientFactory httpClientFactory, InngestSettings settings, ILogger logger)
{
    public InngestSettings Settings => settings;

    // An absolute path on purpose: the API lives at the root of the host, whatever path the base URL has.
    public Uri Url(string pathAndQuery) => new(settings.ApiBaseUrl, pathAndQuery);

    public async Task<HttpResponseMessage> SendAsync(ApiCall call, HttpCompletionOption completionOption, CancellationToken cancellationToken) =>
        (await SendKeyedAsync(call, completionOption, cancellationToken).ConfigureAwait(false)).Response;

    // SendAsync, for callers that report which key Inngest answered to, such as the health check.
    public async Task<KeyedResponse> SendKeyedAsync(ApiCall call, HttpCompletionOption completionOption, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(InngestServiceCollectionExtensions.HttpClientName);
        var response = await SendOnceAsync(client, call, settings.SigningKey, completionOption, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            || call.Token is not null
            || call.Content is not null
            || settings.SigningKeyFallback is not { } fallback)
        {
            return new KeyedResponse(response, UsedFallbackKey: false);
        }

        Log.UsingFallbackSigningKey(logger, (int)response.StatusCode);
        response.Dispose();
        var retried = await SendOnceAsync(client, call, fallback, completionOption, cancellationToken).ConfigureAwait(false);
        return new KeyedResponse(retried, UsedFallbackKey: true);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        HttpClient client,
        ApiCall call,
        SigningKey? key,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(call, key, call.Environment ?? settings.Environment);
        return await client.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
    }

    private static HttpRequestMessage CreateRequest(ApiCall call, SigningKey? key, string? environment)
    {
        var request = new HttpRequestMessage(call.Method, call.Url);
        request.AddSdkHeaders(environment);

        if (call.Body is { } body)
        {
            request.Content = new ReadOnlyMemoryContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        else if (call.Content is not null)
        {
            request.Content = call.Content;
        }

        if (call.SyncKind is not null)
        {
            request.Headers.TryAddWithoutValidation(Headers.SyncKind, call.SyncKind);
        }

        if (call.ExpectedServerKind is not null)
        {
            request.Headers.TryAddWithoutValidation(Headers.ExpectedServerKind, call.ExpectedServerKind);
        }

        if (call.Token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", call.Token);
        }
        else if (key is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.BearerToken);
        }

        return request;
    }
}

// The answer to a request, and whether it came from the fallback key because Inngest refused the signing key.
internal readonly record struct KeyedResponse(HttpResponseMessage Response, bool UsedFallbackKey);

// One request to the REST API. Environment overrides the app's branch environment for this request only.
// Content is a body that can be sent once, such as a stream, so a request with it is never repeated with the fallback
// key. Token authenticates in place of the signing key, as a realtime subscription token does.
internal readonly record struct ApiCall(
    HttpMethod Method,
    Uri Url,
    ReadOnlyMemory<byte>? Body = null,
    string? ExpectedServerKind = null,
    string? SyncKind = null,
    string? Environment = null,
    HttpContent? Content = null,
    string? Token = null);
