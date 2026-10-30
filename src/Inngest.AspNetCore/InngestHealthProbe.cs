// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using Inngest.Client;
using Inngest.Configuration;
using Inngest.Registration;
using Microsoft.Extensions.Logging;

namespace Inngest.AspNetCore;

// Asks Inngest whether it answers and takes the app's key, and keeps the answer for the probes that follow. One
// instance serves every probe of a registration, in whatever scope the health check service runs it, so it holds
// nothing scoped. A probe that arrives while a request is out waits for that request instead of sending its own.
internal sealed class InngestHealthProbe
{
    private readonly InngestApiTransport _transport;
    private readonly InngestSettings _settings;
    private readonly InngestHealthPolicy _policy;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Replaced as a whole, so a reader sees one result with its own timestamp.
    private volatile Remembered? _last;

    public InngestHealthProbe(
        IHttpClientFactory httpClientFactory,
        InngestSettings settings,
        IFunctionRegistry? registry,
        InngestHealthPolicy policy,
        TimeProvider time,
        ILogger logger)
    {
        _transport = new InngestApiTransport(httpClientFactory, settings, logger);
        _settings = settings;
        _policy = policy;
        _time = time;
        ServedFunctions = registry?.Functions.Count ?? 0;
    }

    // The functions this instance registers when it syncs, which is what Inngest should know of the app.
    public int ServedFunctions { get; }

    // The remembered result while it is fresh, else a new one. Cancelling the probe stops its wait for the gate and
    // for Inngest and stores nothing. A request that runs past the timeout is a result like any other failure, and
    // only the caller's cancellation is thrown.
    public async Task<ProbeResult> GetAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (TryReuse(out var result))
        {
            return result;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another probe may have asked while this one waited for the gate.
            if (TryReuse(out result))
            {
                return result;
            }

            result = await AskAsync(timeout, cancellationToken).ConfigureAwait(false);
            _last = new Remembered(result, _time.GetTimestamp());
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryReuse([NotNullWhen(true)] out ProbeResult? result)
    {
        if (_last is { } last && _time.GetElapsedTime(last.Timestamp) < _policy.LifetimeOf(last.Result))
        {
            result = last.Result;
            return true;
        }

        result = null;
        return false;
    }

    private async Task<ProbeResult> AskAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        // The deadline runs on the time provider, like the cache does, so a test moves both with one clock. One
        // deadline covers both requests: the check is as slow as its timeout, never slower.
        using var deadline = new CancellationTokenSource(timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var (api, statusCode, usedFallbackKey) = await RequestApiAsync(linked.Token).ConfigureAwait(false);
            if (api != ApiStatus.Ok || !_policy.CheckSync)
            {
                return Result(api, statusCode, usedFallbackKey);
            }

            return await RequestSyncAsync(statusCode, usedFallbackKey, linked.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            return Result(ApiStatus.Unreachable, 0, usedFallbackKey: false, Shorten(exception.Message));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result(ApiStatus.TimedOut, 0, usedFallbackKey: false);
        }
    }

    private async Task<(ApiStatus Api, int StatusCode, bool UsedFallbackKey)> RequestApiAsync(CancellationToken cancellationToken)
    {
        // Dev servers of every version answer /health. The authenticated read is for servers that hold a signing key:
        // Inngest Cloud answers /v2/health to anyone, so it would pass with a key Inngest has never seen.
        var path = _settings.IsDev ? "/health" : "/v2/apps?limit=1";
        var answer = await _transport.SendKeyedAsync(
            new ApiCall(HttpMethod.Get, _transport.Url(path)),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        using var response = answer.Response;
        return (Classify(response.StatusCode), (int)response.StatusCode, answer.UsedFallbackKey);
    }

    // Inngest's Get app endpoint. A 404 is the answer to "is the app registered" and not a failure of the request,
    // because the request before it has shown that the API is there and takes the key.
    private async Task<ProbeResult> RequestSyncAsync(int apiStatusCode, bool usedFallbackKey, CancellationToken cancellationToken)
    {
        var url = _transport.Url($"/v2/apps/{Uri.EscapeDataString(_settings.AppId)}");
        var answer = await _transport.SendKeyedAsync(new ApiCall(HttpMethod.Get, url), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        using var response = answer.Response;
        usedFallbackKey |= answer.UsedFallbackKey;

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return Result(ApiStatus.Ok, apiStatusCode, usedFallbackKey, sync: new SyncResult(SyncVerdict.NotRegistered));
        }

        var api = Classify(response.StatusCode);
        if (api != ApiStatus.Ok)
        {
            return Result(api, (int)response.StatusCode, usedFallbackKey);
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                using var body = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                var sync = SyncResult.Read(body.RootElement, ServedFunctions, _settings.AppVersion);
                return Result(ApiStatus.Ok, apiStatusCode, usedFallbackKey, sync: sync);
            }
            catch (JsonException)
            {
                return Result(ApiStatus.Ok, apiStatusCode, usedFallbackKey, sync: new SyncResult(SyncVerdict.Unreadable));
            }
        }
    }

    private static ApiStatus Classify(HttpStatusCode status) => (int)status switch
    {
        >= 200 and <= 299 => ApiStatus.Ok,
        401 or 403 => ApiStatus.Refused,
        429 => ApiStatus.RateLimited,
        _ => ApiStatus.Unexpected,
    };

    private ProbeResult Result(ApiStatus api, int statusCode, bool usedFallbackKey, string? detail = null, SyncResult? sync = null) =>
        new(api, statusCode, usedFallbackKey, detail, _time.GetUtcNow(), sync);

    // What a gateway or a proxy says can run long; a description should not.
    private static string Shorten(string message) => message.Length <= 200 ? message : message[..200] + "...";

    private sealed record Remembered(ProbeResult Result, long Timestamp);
}

internal enum ApiStatus
{
    Ok,
    Refused,
    Unreachable,
    TimedOut,
    RateLimited,
    Unexpected,
}

// What the requests to Inngest found. StatusCode is that of the first request, and 0 when there was no answer; Detail
// is the reason a request failed without one. Sync is set when the check asked about the app and Api is Ok. Nothing
// here holds anything of a request: no key and no token.
internal sealed record ProbeResult(ApiStatus Api, int StatusCode, bool UsedFallbackKey, string? Detail, DateTimeOffset CheckedAt, SyncResult? Sync = null)
{
    public bool IsHealthy => Api == ApiStatus.Ok && Sync is null or { IsSynced: true };
}
