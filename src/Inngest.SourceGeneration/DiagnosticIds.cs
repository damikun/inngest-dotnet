// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

namespace Inngest.SourceGeneration;

internal static class DiagnosticIds
{
    public const string NotAFunction = "INNGEST001";
    public const string AbstractFunction = "INNGEST002";
    public const string GenericFunction = "INNGEST003";
    public const string InaccessibleFunction = "INNGEST004";
    public const string NoPublicConstructor = "INNGEST005";
    public const string InvalidOption = "INNGEST006";
    public const string TypeNotInJsonContext = "INNGEST010";
    public const string AnonymousType = "INNGEST011";
    public const string StateAssignedInStep = "INNGEST020";
    public const string CleanupAroundStep = "INNGEST021";
    public const string NondeterministicStepId = "INNGEST022";
    public const string BlockingOnStep = "INNGEST023";
    public const string NestedStep = "INNGEST024";
    public const string StepNotAwaited = "INNGEST025";
    public const string NondeterministicValue = "INNGEST026";
    public const string DelayInFunction = "INNGEST027";
    public const string AwaitOutsideStep = "INNGEST028";
    public const string UnorderedLoopOverSteps = "INNGEST029";
    public const string InvalidCronSchedule = "INNGEST030";
    public const string InvalidTimeString = "INNGEST031";
    public const string RepeatedStepId = "INNGEST032";
    public const string EventNameMismatch = "INNGEST033";
}
