using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.Evaluation.Harness;

// Every request the SDK made to Inngest through its HTTP client: syncs, event sends and the run state it fetches when a
// call says use_api. A singleton in the app under test.
public sealed class OutgoingRequests
{
    private readonly ConcurrentQueue<OutgoingRequest> _requests = new();

    public IReadOnlyList<OutgoingRequest> Snapshot() => [.. _requests];

    public void Add(OutgoingRequest request) => _requests.Enqueue(request);
}

// StatusCode is null when the request failed without a response.
public sealed record OutgoingRequest(DateTimeOffset At, string Method, string PathAndQuery, string? Authorization, int? StatusCode)
{
    public override string ToString() => $"{At:HH:mm:ss.fff} {Method} {PathAndQuery} -> {StatusCode?.ToString() ?? "no response"}";
}

public static class OutgoingRequestsExtensions
{
    // The SDK sends everything through the named client "Inngest"; a handler added to it sees every call.
    public static IInngestBuilder RecordOutgoingRequests(this IInngestBuilder inngest)
    {
        inngest.Services.AddSingleton<OutgoingRequests>();
        inngest.Services.AddHttpClient("Inngest")
            .AddHttpMessageHandler(services => new RecordingHandler(services.GetRequiredService<OutgoingRequests>()));
        return inngest;
    }

    private sealed class RecordingHandler(OutgoingRequests log) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var at = DateTimeOffset.UtcNow;
            var authorization = request.Headers.Authorization?.ToString();
            var target = request.RequestUri?.PathAndQuery ?? string.Empty;
            try
            {
                var response = await base.SendAsync(request, cancellationToken);
                log.Add(new OutgoingRequest(at, request.Method.Method, target, authorization, (int)response.StatusCode));
                return response;
            }
            catch
            {
                log.Add(new OutgoingRequest(at, request.Method.Method, target, authorization, null));
                throw;
            }
        }
    }
}
