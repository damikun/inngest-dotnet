// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Runtime.InteropServices;
using System.Text.Json;
using Google.Protobuf;
using Inngest.Connect.Protocol;
using Inngest.Internal;
using Inngest.Registration;
using Timestamp = Google.Protobuf.WellKnownTypes.Timestamp;

namespace Inngest.Connect;

// The WORKER_CONNECT message, which tells the gateway who the worker is and which functions it runs. The gateway syncs
// the app from it, so the function configuration is the one an HTTP sync sends, with steps addressed by a ws:// URL.
// Everything except the tokens of the connection is worked out once.
internal sealed class WorkerHello
{
    private const string Framework = "connect";
    private const string DeployType = "connect";

    // The gateway only uses the fnId of a step's URL; the host is a placeholder, as in the TypeScript SDK.
    private static readonly Uri StepBaseUrl = new("wss://connect");

    private readonly InngestSettings _settings;
    private readonly ConnectSettings _connect;
    private readonly AppConfiguration _app;
    private readonly ByteString _capabilities;
    private readonly SystemAttributes _system;

    public WorkerHello(InngestSettings settings, ConnectSettings connect, IFunctionRegistry registry)
    {
        _settings = settings;
        _connect = connect;

        using var registration = new PooledBufferWriter(4096);
        registry.WriteRegistration(registration, StepBaseUrl, Framework, DeployType);
        using var document = JsonDocument.Parse(registration.WrittenMemory);
        var body = document.RootElement;

        _app = new AppConfiguration
        {
            AppName = settings.AppId,
            Functions = ByteString.CopyFrom(JsonMarshal.GetRawUtf8Value(body.GetProperty("functions"))),
        };

        if (settings.AppVersion is { } appVersion)
        {
            _app.AppVersion = appVersion;
        }

        _capabilities = ByteString.CopyFrom(JsonMarshal.GetRawUtf8Value(body.GetProperty("capabilities")));
        _system = new SystemAttributes
        {
            CpuCores = Environment.ProcessorCount,
            MemBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            Os = OperatingSystemName(),
        };
    }

    public WorkerConnectRequestData Create(StartResponse start, DateTimeOffset startedAt)
    {
        var hello = new WorkerConnectRequestData
        {
            ConnectionId = start.ConnectionId,
            InstanceId = _connect.InstanceId,
            AuthData = new AuthData { SessionToken = start.SessionToken, SyncToken = start.SyncToken },
            Capabilities = _capabilities,
            Apps = { _app },

            // The gateway holds requests back until WORKER_READY, which the worker sends once it can take them.
            WorkerManualReadinessAck = true,
            SystemAttributes = _system,
            Framework = Framework,
            SdkLanguage = SdkInfo.Language,
            SdkVersion = $"v{SdkInfo.Version}",
            StartedAt = Timestamp.FromDateTimeOffset(startedAt),
        };

        if (_settings.Environment is { } environment)
        {
            hello.Environment = environment;
        }

        if (_connect.MaxWorkerConcurrency is { } maxConcurrency)
        {
            hello.MaxWorkerConcurrency = maxConcurrency;
        }

        return hello;
    }

    // Named the way Go's runtime.GOOS names them, which is what the other Inngest SDKs on servers report.
    private static string OperatingSystemName() =>
        OperatingSystem.IsLinux() ? "linux"
        : OperatingSystem.IsMacOS() ? "darwin"
        : OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsFreeBSD() ? "freebsd"
        : RuntimeInformation.OSDescription;
}
