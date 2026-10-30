// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using Inngest.Client;
using Inngest.Configuration;
using Inngest.Execution;
using Inngest.Functions;
using Inngest.Registration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Inngest.Testing;

/// <summary>
/// Runs the app's Inngest functions in memory on the real execution engine, playing Inngest's part: it calls the
/// function again after every step, runs planned steps, retries failures, answers sleeps, waits and invokes from
/// mocks and keeps the events the app sends. Nothing leaves the process, and INNGEST_* environment variables are
/// ignored. Runs are independent and may run concurrently.
/// </summary>
public sealed class InngestTestHost : IAsyncDisposable, IDisposable
{
    private const string FunctionFailedEvent = "inngest/function.failed";

    private static readonly StepMocks NoMocks = new();

    private readonly ServiceProvider _provider;
    private readonly IFunctionRegistry _registry;
    private readonly InngestSettings _settings;
    private readonly ConcurrentQueue<InngestEvent> _sent = new();
    private readonly ConcurrentQueue<InngestSignal<JsonElement>> _signals = new();

    /// <summary>
    /// Takes the app's registrations: AddInngest, the functions and middleware, and the services they use. The
    /// collection is copied, so a test can reuse it.
    /// </summary>
    public InngestTestHost(IServiceCollection services)
        : this(services, static engine => engine)
    {
    }

    // decorate wraps or replaces the engine, for tests of the host itself.
    internal InngestTestHost(IServiceCollection services, Func<IExecutionEngine, IExecutionEngine> decorate)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!services.Any(static descriptor => descriptor.ServiceType == typeof(IFunctionRegistry)))
        {
            throw new InvalidOperationException("The services have no Inngest registration. Call AddInngest and add the functions to test.");
        }

        var copy = new ServiceCollection();
        foreach (var descriptor in services)
        {
            copy.Add(descriptor);
        }

        // The client sends only with an event key or in dev mode, although the host keeps every event. Nothing set
        // on the machine may change how a test runs, and no request may reach an Inngest server.
        copy.PostConfigure<InngestOptions>(static options =>
        {
            options.IsDev ??= true;
            if (string.IsNullOrWhiteSpace(options.EventKey))
            {
                options.EventKey = "test";
            }
        });
        copy.Replace(ServiceDescriptor.Singleton(static provider =>
            InngestSettings.Create(provider.GetRequiredService<IOptions<InngestOptions>>().Value, static _ => null)));
        copy.AddHttpClient(InngestServiceCollectionExtensions.HttpClientName).ConfigurePrimaryHttpMessageHandler(static () => new OfflineHandler());
        copy.Replace(ServiceDescriptor.Singleton<ISignalApi>(new RecordingSignalApi(this)));

        // Registered last, so the app's middleware wraps it.
        copy.AddSingleton<InngestMiddleware>(new TestHostMiddleware(this));

        _provider = copy.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        try
        {
            // The registry rejects function options Inngest would reject, as the app does at startup.
            _settings = _provider.GetRequiredService<InngestSettings>();
            _registry = _provider.GetRequiredService<IFunctionRegistry>();
            Time = _provider.GetRequiredService<TimeProvider>();

            // A targeted call gives up on a step the function never reaches after a timeout on the engine's clock.
            // On the system clock it passes even when the app runs on a FakeTimeProvider.
            Engine = decorate(ActivatorUtilities.CreateInstance<ExecutionEngine>(_provider, TimeProvider.System));
        }
        catch
        {
            _provider.Dispose();
            throw;
        }
    }

    /// <summary>The app's services, as functions resolve them.</summary>
    public IServiceProvider Services => _provider;

    /// <summary>Every event sent through the app's Inngest client since the host started, from runs or not.</summary>
    public IReadOnlyList<InngestEvent> SentEvents => [.. _sent];

    /// <summary>
    /// Every signal sent through the app's Inngest client since the host started, from runs or not. None of them
    /// resumes a run: the host runs one function at a time, so the client reports that no run waits for them.
    /// </summary>
    public IReadOnlyList<InngestSignal<JsonElement>> SentSignals => [.. _signals];

    internal TimeProvider Time { get; }

    internal IExecutionEngine Engine { get; }

    internal JsonSerializerOptions SerializerOptions => _settings.SerializerOptions;

    /// <summary>
    /// Runs the function registered under functionId with the event until it completes, fails for good, or waits
    /// on a step the test did not mock.
    /// </summary>
    public Task<TestRun> RunAsync(string functionId, InngestEvent @event, CancellationToken cancellationToken = default) =>
        RunAsync(functionId, @event, NoMocks, cancellationToken);

    public Task<TestRun> RunAsync(string functionId, InngestEvent @event, StepMocks mocks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        return StartAsync(functionId, [@event], mocks, stopAt: null, cancellationToken);
    }

    /// <summary>Runs a function that takes batches with all of the events at once.</summary>
    public Task<TestRun> RunAsync(string functionId, IReadOnlyList<InngestEvent> events, CancellationToken cancellationToken = default) =>
        RunAsync(functionId, events, NoMocks, cancellationToken);

    public Task<TestRun> RunAsync(string functionId, IReadOnlyList<InngestEvent> events, StepMocks mocks, CancellationToken cancellationToken = default) =>
        StartAsync(functionId, events, mocks, stopAt: null, cancellationToken);

    /// <summary>
    /// Runs the function until step stepId has run for good, or has been reached when Inngest carries it out, such
    /// as a sleep or a wait. The run then ends as Stopped, unless it completed, failed or waited before that.
    /// </summary>
    public Task<TestRun> ExecuteStepAsync(string functionId, string stepId, InngestEvent @event, CancellationToken cancellationToken = default) =>
        ExecuteStepAsync(functionId, stepId, @event, NoMocks, cancellationToken);

    public Task<TestRun> ExecuteStepAsync(
        string functionId,
        string stepId,
        InngestEvent @event,
        StepMocks mocks,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(stepId);
        ArgumentNullException.ThrowIfNull(@event);
        return StartAsync(functionId, [@event], mocks, stepId, cancellationToken);
    }

    /// <summary>
    /// Runs the function registered as TFunction, as the overloads that take an id do, so a test of an attribute class
    /// does not repeat the id the attribute gives it.
    /// </summary>
    public Task<TestRun> RunAsync<TFunction>(InngestEvent @event, CancellationToken cancellationToken = default)
        where TFunction : InngestFunction =>
        RunAsync(FunctionIdOf(typeof(TFunction)), @event, cancellationToken);

    public Task<TestRun> RunAsync<TFunction>(InngestEvent @event, StepMocks mocks, CancellationToken cancellationToken = default)
        where TFunction : InngestFunction =>
        RunAsync(FunctionIdOf(typeof(TFunction)), @event, mocks, cancellationToken);

    public Task<TestRun> RunAsync<TFunction>(IReadOnlyList<InngestEvent> events, CancellationToken cancellationToken = default)
        where TFunction : InngestFunction =>
        RunAsync(FunctionIdOf(typeof(TFunction)), events, cancellationToken);

    public Task<TestRun> RunAsync<TFunction>(IReadOnlyList<InngestEvent> events, StepMocks mocks, CancellationToken cancellationToken = default)
        where TFunction : InngestFunction =>
        RunAsync(FunctionIdOf(typeof(TFunction)), events, mocks, cancellationToken);

    public Task<TestRun> ExecuteStepAsync<TFunction>(string stepId, InngestEvent @event, CancellationToken cancellationToken = default)
        where TFunction : InngestFunction =>
        ExecuteStepAsync(FunctionIdOf(typeof(TFunction)), stepId, @event, cancellationToken);

    public Task<TestRun> ExecuteStepAsync<TFunction>(string stepId, InngestEvent @event, StepMocks mocks, CancellationToken cancellationToken = default)
        where TFunction : InngestFunction =>
        ExecuteStepAsync(FunctionIdOf(typeof(TFunction)), stepId, @event, mocks, cancellationToken);

    public void Dispose() => _provider.Dispose();

    public ValueTask DisposeAsync() => _provider.DisposeAsync();

    internal AsyncServiceScope CreateScope() => _provider.CreateAsyncScope();

    // What the host's middleware does instead of sending: the client's checks, then ids as Inngest would give them.
    internal SendEventsResult Record(IReadOnlyList<InngestEvent> events, RunDriver? run)
    {
        var now = Time.GetUtcNow();
        EventJson.ValidateSend(events, SerializerOptions, now);

        var ids = new string[events.Count];
        for (var i = 0; i < ids.Length; i++)
        {
            ids[i] = Ulid.New(now);
            _sent.Enqueue(events[i]);
            run?.Sent(events[i]);
        }

        return new SendEventsResult(ids);
    }

    internal void RecordSignal(InngestSignal<JsonElement> signal, RunDriver? run)
    {
        _signals.Enqueue(signal);
        run?.Signaled(signal);
    }

    private async Task<TestRun> StartAsync(
        string functionId,
        IReadOnlyList<InngestEvent> events,
        StepMocks mocks,
        string? stopAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(functionId);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(mocks);
        if (events.Count == 0)
        {
            throw new ArgumentException("A run needs at least one event.", nameof(events));
        }

        var function = Find(functionId);
        var now = Time.GetUtcNow();
        var payloads = new byte[events.Count][];
        for (var i = 0; i < payloads.Length; i++)
        {
            payloads[i] = EventJson.Serialize(events[i], SerializerOptions, now);
        }

        var run = await new RunDriver(this, function, payloads, mocks, stopAt).RunAsync(cancellationToken).ConfigureAwait(false);

        // As Inngest does, a function that failed for good triggers its failure handler.
        if (run.Status == TestRunStatus.Failed &&
            _registry.TryGet(CompositeId(functionId + "-failure"), out var handler) &&
            handler.FailureHandlerFor == functionId)
        {
            var failed = FailedEvent(functionId, run, payloads[0]);
            run.FailureHandler = await new RunDriver(this, handler, [failed], mocks, stopAt: null).RunAsync(cancellationToken).ConfigureAwait(false);
        }

        return run;
    }

    private FunctionDefinition Find(string functionId)
    {
        if (_registry.TryGet(CompositeId(functionId), out var function))
        {
            return function;
        }

        var registered = string.Join(", ", _registry.Functions.Select(static function => $"\"{function.Id}\""));
        throw new ArgumentException($"No function \"{functionId}\" is registered. Registered functions: {registered}.", nameof(functionId));
    }

    internal string CompositeIdOf(Type functionType) => CompositeId(FunctionIdOf(functionType));

    private string FunctionIdOf(Type functionType) => _registry.FunctionIdOf(functionType);

    private string CompositeId(string functionId) => $"{_settings.AppId}-{functionId}";

    // The inngest/function.failed event Inngest sends once a run has failed for good.
    private byte[] FailedEvent(string functionId, TestRun run, byte[] triggeringEvent)
    {
        var now = Time.GetUtcNow();
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = WireWriter.Create(buffer, SerializerOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("name", FunctionFailedEvent);
            writer.WriteStartObject("data");
            writer.WritePropertyName("error");
            writer.WriteRawValue(run.ErrorJson.Span, skipInputValidation: true);
            writer.WritePropertyName("event");
            writer.WriteRawValue(triggeringEvent, skipInputValidation: true);
            writer.WriteString("function_id", CompositeId(functionId));
            writer.WriteString("run_id", run.RunId);
            writer.WriteEndObject();
            writer.WriteString("id", Ulid.New(now));
            writer.WriteNumber("ts", now.ToUnixTimeMilliseconds());
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }
}
