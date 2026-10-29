using System.Security.Cryptography;
using Inngest.Functions;
using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Inngest.IntegrationTests.Evaluation.Harness;

// An Inngest server and an app with a set of evaluation functions. The functions the server must list once the app is
// synced are read from the app itself, failure handler companions included.
public abstract class EvaluationFixture : InngestFixture, IEvaluationFixture
{
    public override string AppId => "eval";

    public override IReadOnlyCollection<string> ExpectedFunctions =>
        [.. App.Services.GetServices<FunctionDefinition>().Select(function => $"{AppId}-{function.Id}")];

    public Probe Probe => App.Probe;

    public Gates Gates => App.Services.GetRequiredService<Gates>();

    // The dev server accepts any event key.
    protected override string EventKey => "eval";

    protected override InngestServer CreateServer(int hostPort, Uri appUrl) => InngestServer.Dev(hostPort, appUrl);

    protected override void ConfigureInngest(InngestOptions options, Uri serverUrl)
    {
        options.AppId = AppId;
        options.IsDev = true;
        options.DevServerUrl = serverUrl;
    }

    protected override void AddFunctions(IInngestBuilder inngest)
    {
        inngest.Services.AddSingleton<Gates>();
        AddEvaluationFunctions(inngest);
    }

    protected abstract void AddEvaluationFunctions(IInngestBuilder inngest);
}

// The dev server and the app most scenarios share: functions for triggers, steps, sleeps and waits, invoke, sendEvent,
// errors and flow control. Scenarios isolate themselves through their test id, and flow control is keyed by it.
public sealed class EvaluationDevFixture : EvaluationFixture
{
    protected override void AddEvaluationFunctions(IInngestBuilder inngest)
    {
        TriggerFunctions.Add(inngest);
        StepFunctions.Add(inngest);
        StepDataFunctions.Add(inngest);
        ParallelStepFunctions.Add(inngest);
        SleepFunctions.Add(inngest);
        WaitForEventFunctions.Add(inngest);
        InvokeFunctions.Add(inngest);
        SendEventFunctions.Add(inngest);
        RetryFunctions.Add(inngest);
        FailureFunctions.Add(inngest);
        ConcurrencyFunctions.Add(inngest);
        RateFunctions.Add(inngest);
        DebounceAndBatchFunctions.Add(inngest);
        SingletonAndTimeoutFunctions.Add(inngest);
        CancellationFunctions.Add(inngest);
    }
}

// An app with middleware, which applies to every function of an app, so it gets a server of its own.
public sealed class MiddlewareFixture : EvaluationFixture
{
    protected override void AddEvaluationFunctions(IInngestBuilder inngest) => MiddlewareFunctions.Add(inngest);
}

// Large and many runs, on a server of their own so that they do not slow down the timing-sensitive scenarios.
public sealed class LoadFixture : EvaluationFixture
{
    protected override void AddEvaluationFunctions(IInngestBuilder inngest)
    {
        inngest.RecordOutgoingRequests();
        LoadFunctions.Add(inngest);
        LargeStateFunctions.Add(inngest);
    }
}

// An app that scenarios stop and start again on the same port while the server keeps its runs. What the functions
// record survives the restart, and each app instance has an id of its own.
public sealed class RestartFixture : EvaluationFixture
{
    private readonly Probe _probe = new();
    private bool _stopped;

    public string InstanceId => App.Services.GetRequiredService<AppInstance>().Id;

    // Stops the app as a deployment would: Kestrel stops taking requests and waits for running ones up to the host's
    // shutdown timeout, which is short here, before it aborts them.
    public async Task StopAppAsync()
    {
        _stopped = true;
        await App.DisposeAsync();
    }

    public async Task StartAppAsync(CancellationToken cancellationToken)
    {
        App = await TestApp.StartAsync(App.Port, options => ConfigureInngest(options, Server.Url), AddFunctions, cancellationToken);
        _stopped = false;
    }

    // A scenario that failed while the app was down leaves it stopped; the next scenario starts it again.
    public async Task EnsureRunningAsync(CancellationToken cancellationToken)
    {
        if (_stopped)
        {
            await StartAppAsync(cancellationToken);
        }
    }

    protected override void AddEvaluationFunctions(IInngestBuilder inngest)
    {
        inngest.Services.AddSingleton(_probe);
        inngest.Services.AddSingleton<AppInstance>();
        inngest.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(1));
        RestartFunctions.Add(inngest);
    }
}

// The app in cloud mode against a self-hosted server (inngest start) that signs every request with the app's key.
public sealed class CloudFixture : EvaluationFixture
{
    // inngest start only accepts plain hex of even length.
    public string SigningKey { get; } = NewKey();

    public string ServerEventKey { get; } = $"eval-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}";

    protected override string EventKey => ServerEventKey;

    protected override string? ApiKey => SigningKey;

    public static string NewKey() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    protected override InngestServer CreateServer(int hostPort, Uri appUrl) =>
        InngestServer.SelfHosted(hostPort, appUrl, SigningKey, ServerEventKey);

    protected override void ConfigureInngest(InngestOptions options, Uri serverUrl)
    {
        options.AppId = AppId;
        options.IsDev = false;
        options.SigningKey = SigningKey;
        options.EventKey = ServerEventKey;
        options.BaseUrl = serverUrl;
    }

    protected override void AddEvaluationFunctions(IInngestBuilder inngest)
    {
        inngest.RecordOutgoingRequests();
        CloudFunctions.Add(inngest);
    }
}

// A signing key rotation in progress: the server still signs with the old key, while the app has been deployed with the
// new key and the old one as its fallback, as Inngest's rotation guide describes.
public sealed class RotationFixture : EvaluationFixture
{
    public string OldKey { get; } = CloudFixture.NewKey();

    public string NewKey { get; } = CloudFixture.NewKey();

    public string ServerEventKey { get; } = $"eval-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}";

    protected override string EventKey => ServerEventKey;

    protected override string? ApiKey => OldKey;

    protected override InngestServer CreateServer(int hostPort, Uri appUrl) =>
        InngestServer.SelfHosted(hostPort, appUrl, OldKey, ServerEventKey);

    protected override void ConfigureInngest(InngestOptions options, Uri serverUrl)
    {
        options.AppId = AppId;
        options.IsDev = false;
        options.SigningKey = NewKey;
        options.SigningKeyFallback = OldKey;
        options.EventKey = ServerEventKey;
        options.BaseUrl = serverUrl;
    }

    protected override void AddEvaluationFunctions(IInngestBuilder inngest)
    {
        inngest.RecordOutgoingRequests();
        RotationFunctions.Add(inngest);
    }
}

// Identifies one start of the app under test.
public sealed class AppInstance
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
}
