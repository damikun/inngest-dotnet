using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using Google.Protobuf;
using Inngest.Connect;
using Inngest.Connect.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Inngest.Tests.Connect.Support;

// The Inngest API and a gateway in the test process: Kestrel on a loopback port serving POST /v0/connect/start,
// POST /v0/connect/flush and the WebSocket at /v0/connect. Every socket the worker opens becomes a GatewaySession,
// which runs the handshake and then lets the test drive the conversation.
internal sealed class FakeGateway : IAsyncDisposable
{
    public const string GatewayGroup = "test-group";

    private readonly WebApplication _app;
    private readonly Channel<GatewaySession> _sessions = Channel.CreateUnbounded<GatewaySession>();
    private readonly Channel<FlushCall> _flushes = Channel.CreateUnbounded<FlushCall>();
    private readonly ConcurrentQueue<StartCall> _starts = new();
    private readonly ConcurrentQueue<GatewaySession> _allSessions = new();
    private int _connections;

    private FakeGateway(WebApplication app)
    {
        _app = app;
        BaseUrl = new Uri(app.Urls.Single());
        GatewayUrl = new Uri($"ws://{BaseUrl.Authority}/v0/connect");
    }

    public Uri BaseUrl { get; }

    public Uri GatewayUrl { get; }

    // What GATEWAY_CONNECTION_READY announces.
    public string HeartbeatInterval { get; set; } = "10s";

    public string ExtendLeaseInterval { get; set; } = "30s";

    // The gateway answers heartbeats and acknowledges replies unless a test turns that off.
    public bool AnswerHeartbeats { get; set; } = true;

    public bool AcknowledgeReplies { get; set; } = true;

    // Answers a start request with this status instead of a gateway, when it returns one.
    public Func<StartCall, HttpStatusCode?>? StartStatus { get; set; }

    // Where the start response sends the worker; the gateway itself by default.
    public string? AdvertisedEndpoint { get; set; }

    // The message that opens every session; a real gateway always sends GATEWAY_HELLO.
    public Func<GatewayMessageType> FirstMessage { get; set; } = static () => GatewayMessageType.GatewayHello;

    // True for a session that should stay silent instead of opening with GATEWAY_HELLO, for tests of a gateway that
    // accepts the socket and then says nothing. The session ends when the worker gives up and closes the socket.
    public Func<bool>? WithholdHello { get; set; }

    // Runs after WORKER_CONNECT arrives. Returning false skips GATEWAY_CONNECTION_READY, for tests of failed handshakes.
    public Func<GatewaySession, Task<bool>>? OnWorkerConnect { get; set; }

    // Answers a flush with this status instead of 200, when it returns one.
    public Func<FlushCall, HttpStatusCode?>? FlushStatus { get; set; }

    public IReadOnlyList<StartCall> Starts => [.. _starts];

    public IReadOnlyList<GatewaySession> Sessions => [.. _allSessions];

    public static async Task<FakeGateway> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();

        var app = builder.Build();
        FakeGateway? gateway = null;
        app.UseWebSockets();
        app.MapPost("/v0/connect/start", context => gateway!.StartAsync(context));
        app.MapPost("/v0/connect/flush", context => gateway!.FlushAsync(context));
        app.Map("/v0/connect", context => gateway!.AcceptAsync(context));
        await app.StartAsync(TestContext.Current.CancellationToken);

        gateway = new FakeGateway(app);
        return gateway;
    }

    public async Task<GatewaySession> NextSessionAsync()
    {
        try
        {
            return await _sessions.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Waits.Timeout);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"The worker did not complete a handshake. Start requests so far: {_starts.Count}.", exception);
        }
    }

    // A session that is up and has been told the worker is ready for requests.
    public async Task<GatewaySession> NextReadySessionAsync()
    {
        var session = await NextSessionAsync();
        await session.NextAsync(GatewayMessageType.WorkerReady);
        return session;
    }

    public async Task<FlushCall> NextFlushAsync()
    {
        try
        {
            return await _flushes.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Waits.Timeout);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException("The worker did not flush a reply.", exception);
        }
    }

    public bool TryTakeFlush(out FlushCall flush) => _flushes.Reader.TryRead(out flush!);

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _allSessions)
        {
            session.Abort();
        }

        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
    }

    private async Task StartAsync(HttpContext context)
    {
        var body = await ReadBodyAsync(context);
        var call = new StartCall(
            context.Request.Headers.Authorization.ToString(),
            context.Request.ContentType,
            context.Request.Headers["X-Inngest-Env"].ToString(),
            context.Request.Headers["X-Inngest-SDK"].ToString(),
            StartRequest.Parser.ParseFrom(body),
            body.Length);
        _starts.Enqueue(call);

        if (StartStatus?.Invoke(call) is { } status)
        {
            context.Response.StatusCode = (int)status;
            await context.Response.WriteAsync("Authentication failed");
            return;
        }

        var number = _starts.Count;
        var response = new StartResponse
        {
            ConnectionId = $"01JCONNECTION{number:D13}",
            GatewayEndpoint = AdvertisedEndpoint ?? GatewayUrl.AbsoluteUri,
            GatewayGroup = GatewayGroup,
            SessionToken = $"session-{number}",
            SyncToken = $"sync-{number}",
        };

        context.Response.ContentType = "application/octet-stream";
        await context.Response.Body.WriteAsync(response.ToByteArray());
    }

    private async Task FlushAsync(HttpContext context)
    {
        var body = await ReadBodyAsync(context);
        var call = new FlushCall(context.Request.Headers.Authorization.ToString(), context.Request.ContentType, SDKResponse.Parser.ParseFrom(body));
        if (FlushStatus?.Invoke(call) is { } status)
        {
            context.Response.StatusCode = (int)status;
            await _flushes.Writer.WriteAsync(call with { Status = status });
            return;
        }

        context.Response.ContentType = "application/octet-stream";
        await context.Response.Body.WriteAsync(new FlushResponse { RequestId = call.Reply.RequestId }.ToByteArray());
        await _flushes.Writer.WriteAsync(call);
    }

    private async Task AcceptAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var protocols = context.WebSockets.WebSocketRequestedProtocols;
        var socket = await context.WebSockets.AcceptWebSocketAsync(protocols.FirstOrDefault());
        var session = new GatewaySession(this, socket, Interlocked.Increment(ref _connections), [.. protocols]);
        _allSessions.Enqueue(session);
        await session.RunAsync(PublishAsync);
    }

    private ValueTask PublishAsync(GatewaySession session) => _sessions.Writer.WriteAsync(session);

    private static async Task<byte[]> ReadBodyAsync(HttpContext context)
    {
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer);
        return buffer.ToArray();
    }
}

internal sealed record StartCall(string Authorization, string? ContentType, string Environment, string Sdk, StartRequest Request, int BodyLength);

internal sealed record FlushCall(string Authorization, string? ContentType, SDKResponse Reply)
{
    public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
}

internal static class Waits
{
    // Generous, so that a slow machine does not fail a test, and well under the test timeout.
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
}

internal static class GatewayMessages
{
    public static GatewayExecutorRequestData Request(
        string requestId,
        string functionSlug = "shop-orders",
        string payload = RequestPayloads.Simple,
        string leaseId = "01JLEASE00000000000000000001",
        string? stepId = "step",
        string appName = "shop") =>
        new GatewayExecutorRequestData
        {
            RequestId = requestId,
            AccountId = "account-1",
            EnvId = "env-1",
            AppId = "app-1",
            AppName = appName,
            FunctionId = "function-1",
            FunctionSlug = functionSlug,
            RequestPayload = ByteString.CopyFromUtf8(payload),
            SystemTraceCtx = ByteString.CopyFromUtf8("""{"traceparent":"00-system-01"}"""),
            UserTraceCtx = ByteString.CopyFromUtf8("""{"traceparent":"00-user-01","tracestate":"user=1"}"""),
            RunId = "01JRUN000000000000000000001",
            LeaseId = leaseId,
            JobId = requestId,
        }.WithStep(stepId);

    private static GatewayExecutorRequestData WithStep(this GatewayExecutorRequestData request, string? stepId)
    {
        if (stepId is not null)
        {
            request.StepId = stepId;
        }

        return request;
    }
}

internal static class RequestPayloads
{
    // A first call of a run, as Inngest sends it over HTTP.
    public const string Simple =
        """{"ctx":{"attempt":0,"disable_immediate_execution":false,"fn_id":"function-1","max_attempts":4,"run_id":"01JRUN000000000000000000001","stack":{"current":0,"stack":[]},"use_api":false},"event":{"data":{"orderId":42},"id":"01JEVENT","name":"order/created","ts":1793268000000,"user":{}},"events":[{"data":{"orderId":42},"id":"01JEVENT","name":"order/created","ts":1793268000000,"user":{}}],"steps":{},"version":2}""";
}

internal static class FrameTestExtensions
{
    // The encoded form of a message, as the worker or the gateway put it on the socket.
    public static byte[] Encode(GatewayMessageType kind, IMessage? payload = null)
    {
        using var frame = Frames.Create(kind, payload);
        return frame.Memory.ToArray();
    }
}
