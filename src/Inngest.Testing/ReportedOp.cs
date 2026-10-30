// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Globalization;
using System.Text.Json;

namespace Inngest.Testing;

// One operation of a 206 answer, as the SDK writes it for Inngest. Data and Error point into the answer and are
// only valid while it is being handled.
internal readonly record struct ReportedOp(
    string HashedId,
    string Code,
    string Id,
    StepType? Type,
    bool Runnable,
    bool Races,
    JsonElement Options,
    JsonElement Data,
    JsonElement Error)
{
    public const string StepPlanned = "StepPlanned";
    public const string StepRun = "StepRun";
    public const string StepError = "StepError";
    public const string StepFailed = "StepFailed";
    public const string Sleep = "Sleep";
    public const string WaitForEvent = "WaitForEvent";
    public const string InvokeFunction = "InvokeFunction";
    public const string WaitForSignal = "WaitForSignal";
    public const string Gateway = "Gateway";
    public const string AIGateway = "AIGateway";

    // The step types the host carries out or mocks. Others leave the run waiting on them.
    public static bool IsSupported(StepType? type) =>
        type is not null;

    public static ReportedOp Read(JsonElement op)
    {
        var hashedId = Text(op, "id") ?? throw new JsonException("The SDK reported an operation without an id.");
        var code = Text(op, "op") ?? string.Empty;
        var options = op.TryGetProperty("opts", out var opts) && opts.ValueKind == JsonValueKind.Object ? opts.Clone() : default;
        var runnable = code is StepPlanned or StepRun or StepError or StepFailed;
        StepType? type = code switch
        {
            _ when runnable => Text(options, "type") switch
            {
                "step.sendEvent" => StepType.SendEvent,
                "step.sendSignal" => StepType.SendSignal,
                "step.ai.wrap" => StepType.AIWrap,
                "step.realtime.publish" => StepType.RealtimePublish,
                _ => StepType.Run,
            },
            Sleep => StepType.Sleep,
            WaitForEvent => StepType.WaitForEvent,
            InvokeFunction => StepType.Invoke,

            WaitForSignal => StepType.WaitForSignal,
            Gateway => StepType.Fetch,
            AIGateway => StepType.AIInfer,
            _ => null,
        };

        return new ReportedOp(
            hashedId,
            code,
            FinalId(op) ?? Text(op, "displayName") ?? hashedId,
            type,
            runnable,
            string.Equals(Text(options, "parallelMode"), "race", StringComparison.OrdinalIgnoreCase),
            options,
            op.TryGetProperty("data", out var data) ? data : default,
            op.TryGetProperty("error", out var error) ? error : default);
    }

    public static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // The id the code passed, with the ":<n>" suffix of a repeated id.
    private static string? FinalId(JsonElement op)
    {
        if (!op.TryGetProperty("userland", out var userland) || Text(userland, "id") is not { } id)
        {
            return null;
        }

        return userland.TryGetProperty("index", out var index) && index.TryGetInt32(out var number)
            ? string.Create(CultureInfo.InvariantCulture, $"{id}:{number}")
            : id;
    }
}
