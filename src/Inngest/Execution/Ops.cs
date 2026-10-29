// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;

namespace Inngest.Execution;

// The op codes the SDK sends, as Inngest's opcode enum spells them.
internal static class Ops
{
    public static readonly JsonEncodedText StepPlanned = JsonEncodedText.Encode("StepPlanned");
    public static readonly JsonEncodedText StepRun = JsonEncodedText.Encode("StepRun");
    public static readonly JsonEncodedText StepError = JsonEncodedText.Encode("StepError");
    public static readonly JsonEncodedText StepFailed = JsonEncodedText.Encode("StepFailed");
    public static readonly JsonEncodedText Sleep = JsonEncodedText.Encode("Sleep");
    public static readonly JsonEncodedText WaitForEvent = JsonEncodedText.Encode("WaitForEvent");
    public static readonly JsonEncodedText InvokeFunction = JsonEncodedText.Encode("InvokeFunction");
    public static readonly JsonEncodedText WaitForSignal = JsonEncodedText.Encode("WaitForSignal");
    public static readonly JsonEncodedText Gateway = JsonEncodedText.Encode("Gateway");
    public static readonly JsonEncodedText AIGateway = JsonEncodedText.Encode("AIGateway");
}
