// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Net;
using System.Net.Http.Headers;
using Google.Protobuf;
using Inngest.Client;
using Inngest.Connect.Protocol;
using Inngest.Internal;
using Inngest.Signing;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Connect;

// The two HTTP calls of Connect: /v0/connect/start, which hands out a gateway and session tokens for one
// connection, and /v0/connect/flush, which takes a reply the gateway never acknowledged. Both speak protobuf and
// authenticate with the hashed signing key; the dev server needs no key.
internal sealed class ConnectApi
{
    private static readonly MediaTypeHeaderValue ProtobufContentType = new("application/protobuf");

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly InngestSettings _settings;

    public ConnectApi(IHttpClientFactory httpClientFactory, InngestSettings settings)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;

        // Absolute paths: the API lives at the root of the host, whatever path the base URL has.
        StartUrl = new Uri(settings.ApiBaseUrl, "/v0/connect/start");
        FlushUrl = new Uri(settings.ApiBaseUrl, "/v0/connect/flush");
    }

    public Uri StartUrl { get; }

    public Uri FlushUrl { get; }

    // Throws ConnectException when there is no usable answer, saying whether the key was rejected.
    public async Task<StartResponse> StartAsync(SigningKey? key, IReadOnlyCollection<string> excludedGateways, CancellationToken cancellationToken)
    {
        var body = new StartRequest();
        body.ExcludeGateways.AddRange(excludedGateways);

        using var request = CreateRequest(StartUrl, key, body.ToByteArray());
        HttpResponseMessage response;
        try
        {
            response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new ConnectException(ConnectFailure.Unreachable, $"Could not reach Inngest at {StartUrl}: {exception.Message}", exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ConnectException(ConnectFailure.Unreachable, $"The request to {StartUrl} timed out.", exception);
        }

        using (response)
        {
            var content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    try
                    {
                        return StartResponse.Parser.ParseFrom(content);
                    }
                    catch (InvalidProtocolBufferException exception)
                    {
                        throw new ConnectException(ConnectFailure.Rejected, $"Inngest answered {StartUrl} with a body that is not a start response.", exception);
                    }

                case HttpStatusCode.Unauthorized:
                    throw new ConnectException(ConnectFailure.Unauthorized, $"Inngest rejected the signing key ({status}): {Describe(content)}");
                case HttpStatusCode.TooManyRequests:
                    throw new ConnectException(ConnectFailure.ConnectionLimit, $"Inngest refused the connection because the limit of connections is reached ({status}): {Describe(content)}");
                default:
                    throw new ConnectException(ConnectFailure.Rejected, $"Inngest answered {StartUrl} with {status}: {Describe(content)}");
            }
        }
    }

    // Posts one encoded SDKResponse and returns the status Inngest answered with; 200 means it took the reply.
    // Inngest stores it like a reply over the socket, so sending one twice does no harm. A rejected key is tried once
    // more with the other key, as with every call to the Inngest API.
    public async Task<HttpStatusCode> FlushAsync(SigningKey? key, ReadOnlyMemory<byte> reply, CancellationToken cancellationToken)
    {
        var status = await SendFlushAsync(key, reply, cancellationToken).ConfigureAwait(false);
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && OtherKey(key) is { } other)
        {
            status = await SendFlushAsync(other, reply, cancellationToken).ConfigureAwait(false);
        }

        return status;
    }

    // The key to try after this one was rejected: the fallback key while it is being rotated in, and back again.
    public SigningKey? OtherKey(SigningKey? key) =>
        _settings.SigningKeyFallback is { } fallback && key == _settings.SigningKey ? fallback
        : key == _settings.SigningKeyFallback ? _settings.SigningKey
        : null;

    private HttpClient Client => _httpClientFactory.CreateClient(InngestServiceCollectionExtensions.HttpClientName);

    private async Task<HttpStatusCode> SendFlushAsync(SigningKey? key, ReadOnlyMemory<byte> reply, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(FlushUrl, key, reply);
        using var response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.StatusCode;
    }

    private HttpRequestMessage CreateRequest(Uri url, SigningKey? key, ReadOnlyMemory<byte> body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ReadOnlyMemoryContent(body),
        };
        request.Content.Headers.ContentType = ProtobufContentType;
        request.AddSdkHeaders(_settings.Environment);
        if (key is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.BearerToken);
        }

        return request;
    }

    private static string Describe(byte[] content) => ApiHttp.ToText(content) ?? "no details";
}
