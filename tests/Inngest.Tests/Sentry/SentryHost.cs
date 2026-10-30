using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using Inngest.Execution;
using Inngest.Functions;
using Inngest.Tests.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sentry;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;

namespace Inngest.Tests.Sentry;

// The real engine with AddSentryReporting, called the way the hosts call it: one DI scope per call. Sentry runs for real
// as well, with a transport that sends nowhere, and the host keeps the events Sentry hands over. Sentry's SDK is static,
// so one host may exist at a time, which the test classes guarantee by sharing a collection.
internal sealed class SentryHost : IAsyncDisposable
{
    public const string Collection = "Sentry";

    // A ULID, as the run ids Inngest makes are.
    public const string RunId = "01JD0Z5K9XQ8R3M7N2P4T6V8WB";

    private readonly ServiceProvider _provider;
    private readonly IDisposable? _sdk;
    private readonly ConcurrentQueue<SentryEvent> _events = new();
    private readonly ConcurrentQueue<SentryId> _sent = new();

    // registerHub puts a hub that forwards to Sentry's own into the container, the way UseSentry registers one, and
    // notes the calls it gets.
    public SentryHost(
        Action<SentryReportingOptions>? configure = null,
        bool initialize = true,
        bool registerHub = false,
        Action<IServiceCollection>? services = null)
    {
        if (initialize)
        {
            _sdk = SentrySdk.Init(options =>
            {
                options.Dsn = "https://key@o0.ingest.sentry.io/0";
                options.Transport = new RecordingTransport(_sent);
                options.TracesSampleRate = 1;
                options.AutoSessionTracking = false;
                options.EnableBackpressureHandling = false;
                options.DisableAppDomainUnhandledExceptionCapture();
                options.DisableUnobservedTaskExceptionCapture();
                options.DisableAppDomainProcessExitFlush();
                options.SetBeforeSend((@event, _) =>
                {
                    _events.Enqueue(@event);
                    return @event;
                });
            });
        }

        var collection = new ServiceCollection();
        collection.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        collection.AddFakeLogging();
        collection.AddInngest(options =>
        {
            options.AppId = "shop";
            options.IsDev = true;
        }).AddSentryReporting(configure);
        if (registerHub)
        {
            var hub = HubSpy.Forwarding(HubAdapter.Instance);
            Spy = (HubSpy)hub;
            collection.AddSingleton(hub);
        }

        services?.Invoke(collection);
        _provider = collection.BuildServiceProvider();
        Engine = _provider.GetRequiredService<IExecutionEngine>();
    }

    public IExecutionEngine Engine { get; }

    // Null unless registerHub.
    public HubSpy? Spy { get; }

    public IReadOnlyList<SentryEvent> Events => [.. _events];

    // The events that reached the transport, which is what a flush waits for.
    public IReadOnlyList<SentryId> Sent => [.. _sent];

    // disposeSynchronously ends the call's scope with Dispose, as a host that does not know IAsyncDisposable would.
    public async Task<int> CallAsync(
        FunctionDefinition function,
        Call call,
        string? stepId = null,
        string? queueItemId = null,
        string runId = RunId,
        bool disposeSynchronously = false)
    {
        var body = Encoding.UTF8.GetString(call.ToBytes()).Replace("\"run_id\":\"01JRUN\"", $"\"run_id\":\"{runId}\"", StringComparison.Ordinal);
        if (queueItemId is not null)
        {
            body = body.Replace("\"ctx\":{", $"\"ctx\":{{\"qi_id\":\"{queueItemId}\",", StringComparison.Ordinal);
        }

        var scope = _provider.CreateAsyncScope();
        try
        {
            using var result = await Engine.ExecuteAsync(
                new ExecutionRequest
                {
                    Function = function,
                    Body = Encoding.UTF8.GetBytes(body),
                    StepId = stepId ?? "step",
                    Services = scope.ServiceProvider,
                },
                TestContext.Current.CancellationToken);
            return result.StatusCode;
        }
        finally
        {
            if (disposeSynchronously)
            {
                scope.Dispose();
            }
            else
            {
                await scope.DisposeAsync();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        _sdk?.Dispose();
    }

    private sealed class RecordingTransport(ConcurrentQueue<SentryId> sent) : ITransport
    {
        public Task SendEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken = default)
        {
            if (envelope.TryGetEventId() is { } id && envelope.Items.Any(item => item.TryGetType() == "event"))
            {
                sent.Enqueue(id);
            }

            return Task.CompletedTask;
        }
    }
}

internal sealed record HubCall(string Method, object?[] Arguments);

// An IHub that forwards every call to another and notes it.
internal class HubSpy : DispatchProxy
{
    private IHub _hub = null!;

    public ConcurrentQueue<HubCall> Calls { get; } = new();

    public static IHub Forwarding(IHub hub)
    {
        var proxy = Create<IHub, HubSpy>();
        ((HubSpy)proxy)._hub = hub;
        return proxy;
    }

    public IReadOnlyList<string> Methods => [.. Calls.Select(call => call.Method)];

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        Calls.Enqueue(new HubCall(targetMethod!.Name, args ?? []));
        try
        {
            return targetMethod.Invoke(_hub, args);
        }
        catch (TargetInvocationException exception)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException!).Throw();
            throw;
        }
    }
}
