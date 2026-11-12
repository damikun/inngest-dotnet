// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Microsoft.CodeAnalysis;

namespace Inngest.SourceGeneration;

internal static class DiagnosticDescriptors
{
    private const string FunctionsCategory = "Inngest.Functions";
    private const string SerializationCategory = "Inngest.Serialization";
    private const string StepsCategory = "Inngest.Steps";

    // Every diagnostic has a heading of its own in the guide, whose anchor is the lower-case id.
    private const string GuideUrl = "https://damikun.github.io/inngest-dotnet/guides/source-generator/";

    public static readonly DiagnosticDescriptor NotAFunction = new(
        DiagnosticIds.NotAFunction,
        title: "A class with [InngestFunction] is not a function",
        messageFormat: "'{0}' has [InngestFunction] but does not derive from InngestFunction<TEventData> or InngestFunction<TEventData, TResult>, so it is not a function",
        category: FunctionsCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The attribute configures a function class. On any other class it does nothing: the class cannot be registered, by AddFunction<T>() or by AddInngestFunctions(), and Inngest never runs it.",
        helpLinkUri: GuideUrl + "#" + DiagnosticIds.NotAFunction.ToLowerInvariant());

    public static readonly DiagnosticDescriptor AbstractFunction = new(
        DiagnosticIds.AbstractFunction,
        title: "AddInngestFunctions() does not register an abstract function class",
        messageFormat: "'{0}' is abstract, so AddInngestFunctions() does not register it, and the classes that derive from it do not inherit its attributes",
        category: FunctionsCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "An abstract class cannot be created by the service provider, and [InngestFunction] is not inherited. Put the attribute on each concrete class instead.",
        helpLinkUri: GuideUrl + "#" + DiagnosticIds.AbstractFunction.ToLowerInvariant());

    public static readonly DiagnosticDescriptor GenericFunction = new(
        DiagnosticIds.GenericFunction,
        title: "AddInngestFunctions() does not register a generic function class",
        messageFormat: "'{0}' is generic, so AddInngestFunctions() does not register it; register each closed type with AddFunction<T>()",
        category: FunctionsCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The generator cannot know which type arguments the app uses. Register the closed types by hand, or suppress this warning for a class that is only a template.",
        helpLinkUri: GuideUrl + "#" + DiagnosticIds.GenericFunction.ToLowerInvariant());

    public static readonly DiagnosticDescriptor InaccessibleFunction = new(
        DiagnosticIds.InaccessibleFunction,
        title: "AddInngestFunctions() cannot reach a private function class",
        messageFormat: "'{0}' cannot be reached from generated code because it, or a type that contains it, is private, protected, private protected or file-local, so AddInngestFunctions() does not register it",
        category: FunctionsCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The generated method is a static method of its own. Make the class public or internal, with every type that contains it, or register it with AddFunction<T>() from inside the containing type.",
        helpLinkUri: GuideUrl + "#" + DiagnosticIds.InaccessibleFunction.ToLowerInvariant());

    public static readonly DiagnosticDescriptor NoPublicConstructor = new(
        DiagnosticIds.NoPublicConstructor,
        title: "A function class needs a public constructor",
        messageFormat: "'{0}' has no public constructor for the service provider to call, so AddInngestFunctions() does not register it",
        category: FunctionsCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Function classes are created by the service provider, which only calls public constructors. Without one, every call of the function would fail.",
        helpLinkUri: GuideUrl + "#" + DiagnosticIds.NoPublicConstructor.ToLowerInvariant());

    public static readonly DiagnosticDescriptor InvalidOption = new(
        DiagnosticIds.InvalidOption,
        title: "An MSBuild property of the function registration has an invalid value",
        messageFormat: "The MSBuild property {0} is '{1}', which is not {2}; '{3}' is used instead",
        category: FunctionsCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "InngestGenerateFunctionRegistration must be true or false, InngestFunctionsMethodName a C# identifier, and InngestFunctionsAccessibility public or internal.",
        helpLinkUri: GuideUrl + "#" + DiagnosticIds.InvalidOption.ToLowerInvariant());

    public static readonly DiagnosticDescriptor TypeNotInJsonContext = new(
        DiagnosticIds.TypeNotInJsonContext,
        title: "A type that Inngest serializes is missing from the JsonSerializerContext",
        messageFormat: "'{0}' is {1}, but no JsonSerializerContext lists it with [JsonSerializable], and reflection-based JSON is turned off",
        category: SerializationCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "This project turns reflection-based System.Text.Json off, as Native AOT and trimming do, so Inngest can only read and write the types that a source-generated JsonSerializerContext knows. A type that is missing fails with a NotSupportedException when the function runs.",
        helpLinkUri: GuideUrl + "#" + DiagnosticIds.TypeNotInJsonContext.ToLowerInvariant());

    public static readonly DiagnosticDescriptor AnonymousType = new(
        DiagnosticIds.AnonymousType,
        title: "An anonymous type cannot be serialized without reflection",
        messageFormat: "{0} is an anonymous type, which no JsonSerializerContext can list, and reflection-based JSON is turned off",
        category: SerializationCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A JsonSerializerContext lists types by name, and an anonymous type has none. Use a record, a class or a struct and list it with [JsonSerializable].",
        helpLinkUri: GuideUrl + "#" + DiagnosticIds.AnonymousType.ToLowerInvariant());

    public static readonly DiagnosticDescriptor StateAssignedInStep = new(
        DiagnosticIds.StateAssignedInStep,
        title: "A step's handler assigns a variable that the function reads outside the step",
        messageFormat: "'{0}' is assigned in the handler of {1} and read outside it; once the step has a result its handler does not run again, so the function reads what '{0}' held before. Return the value from the step instead.",
        category: StepsCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Inngest calls a function again for every step, and hands the steps that ran before their saved results without running their handlers. A local, a parameter or a field that a handler assigns keeps its old value in every later call, so code after the step sees a different value than in the call that ran it.",
        helpLinkUri: GuideUrl + "#" + DiagnosticIds.StateAssignedInStep.ToLowerInvariant());

    public static readonly DiagnosticDescriptor CleanupAroundStep = new(
        DiagnosticIds.CleanupAroundStep,
        title: "A finally block or a using declaration encloses a step",
        messageFormat: "This {0} encloses {1}; a call that stops at a new step leaves the function without running it, so it runs only in the call that gets past the step",
        category: StepsCategory,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "When a call reaches a step that has no result yet, the SDK answers Inngest and abandons the function: its task never completes, and finally blocks and Dispose calls that enclose the step do not run in that call. A resource or a lock held across a step is released only in the call that goes past it. Acquire it inside the step, or after the steps.",
        helpLinkUri: GuideUrl + "#" + DiagnosticIds.CleanupAroundStep.ToLowerInvariant());

    public static readonly DiagnosticDescriptor NondeterministicStepId = new(
        DiagnosticIds.NondeterministicStepId,
        title: "A step id changes from one call to the next",
        messageFormat: "The id of {0} uses {1}, which differs in every call, so Inngest never finds the step's result and runs it again",
        category: StepsCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Inngest memoizes a step by its id. An id built from the clock, a new Guid or a random number is different in the next call of the function, so the step is new every time, and the run never ends or repeats its work. Build ids from the event's data, or make the value inside a step and use its result.",
        helpLinkUri: GuideUrl + "#" + DiagnosticIds.NondeterministicStepId.ToLowerInvariant());

    public static readonly DiagnosticDescriptor BlockingOnStep = new(
        DiagnosticIds.BlockingOnStep,
        title: "Blocking on the task of a step",
        messageFormat: "{0} blocks on the task of {1}, which deadlocks the call; await it instead",
        category: StepsCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The SDK runs a function on a scheduler of its own, one continuation at a time, and completes a step's task from that scheduler. Blocking the scheduler's thread on the task keeps it from ever completing, and a step that is new never completes in the call at all.",
        helpLinkUri: GuideUrl + "#" + DiagnosticIds.BlockingOnStep.ToLowerInvariant());

    public static readonly DiagnosticDescriptor NestedStep = new(
        DiagnosticIds.NestedStep,
        title: "A step is called inside the handler of another step",
        messageFormat: "{0} is called inside the handler of {1}; steps cannot be nested, so it throws when the handler runs. Call it from the function instead.",
        category: StepsCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A step's handler is the unit Inngest retries and memoizes, and it cannot hold steps of its own. The SDK throws when a handler calls one.",
        helpLinkUri: GuideUrl + "#" + DiagnosticIds.NestedStep.ToLowerInvariant());

    public static readonly DiagnosticDescriptor StepNotAwaited = new(
        DiagnosticIds.StepNotAwaited,
        title: "The task of a step or of SendAsync is dropped",
        messageFormat: "The task of {0} is dropped; await it or return it, or assign it to a discard to show that nothing waits for it",
        category: StepsCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A send that nothing awaits may not have reached Inngest when the function or step returns, and its errors go unseen. A step that nothing awaits lets the code after it go on before the step has run. The compiler warns about such calls in async methods (CS4014); this covers methods and lambdas that are not async, such as a handler written as an Action.",
        helpLinkUri: GuideUrl + "#" + DiagnosticIds.StepNotAwaited.ToLowerInvariant());

    public static readonly DiagnosticDescriptor NondeterministicValue = new(
        DiagnosticIds.NondeterministicValue,
        title: "A function reads the clock or a random value outside a step",
        messageFormat: "{0} gives a different value in every call of the function; get it in a step, so that every call uses the value of the first",
        category: StepsCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Inngest calls a function again for every step and runs its code from the top each time, handing the steps that ran before their saved results. Code outside a step runs in every call, so the time, a new Guid or a random number read there changes from call to call, and a decision made from it can go one way in one call and another way in the next. A value made inside a step is saved with the step's result and is the same in every call.",
        helpLinkUri: GuideUrl + "#" + DiagnosticIds.NondeterministicValue.ToLowerInvariant());

    public static readonly DiagnosticDescriptor DelayInFunction = new(
        DiagnosticIds.DelayInFunction,
        title: "A function waits with Task.Delay or Thread.Sleep",
        messageFormat: "{0} waits in every call of the function and holds the request open; use context.Step.SleepAsync, which waits in Inngest without running anything",
        category: StepsCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A function is called again for every step, so a wait outside a step happens in each of those calls, and while it waits the request stays open, which serverless hosts bill for and time out. SleepAsync is a step: Inngest waits, with no request open, and calls the function when the time is up, once.",
        helpLinkUri: GuideUrl + "#" + DiagnosticIds.DelayInFunction.ToLowerInvariant());

    public static readonly DiagnosticDescriptor AwaitOutsideStep = new(
        DiagnosticIds.AwaitOutsideStep,
        title: "A function awaits a call that is not a step",
        messageFormat: "{0} is awaited outside a step, so it runs again in every call of the function that reaches it; if it writes anything or reads what can change, call it in context.Step.RunAsync",
        category: StepsCategory,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Inngest calls a function again for every step, and code outside a step runs in each of those calls: an email sent there goes out again, and a row read there can differ from the one an earlier call read. Inside a step, the call runs until it succeeds once and its result is saved. Calls that take the function's context or its step tools are not reported, since they can run steps, and neither are the SDK's own calls. It is information only, because a service can be safe to call again, or run steps of its own, as a chat client does with UseInngestSteps and an HttpClient does through the SDK's fetch handler, which this analyzer cannot see.",
        helpLinkUri: GuideUrl + "#" + DiagnosticIds.AwaitOutsideStep.ToLowerInvariant());

    public static readonly DiagnosticDescriptor UnorderedLoopOverSteps = new(
        DiagnosticIds.UnorderedLoopOverSteps,
        title: "A loop calls a step with the same id for each item of an unordered collection",
        messageFormat: "This loop over a {0} calls {1} with an id that does not depend on the item, and the order of a {0} is not defined, so a later call can hand one item's result to another; put the item's key in the id, or sort the items first",
        category: StepsCategory,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "A step id used again gets a number in the order of the calls, so its results are matched to the items by their order. The order of a dictionary or a set is not part of its contract, and with string keys, whose hash codes change from process to process, it can differ between the calls of one run. An id made from the item finds the item's own result in any order.",
        helpLinkUri: GuideUrl + "#" + DiagnosticIds.UnorderedLoopOverSteps.ToLowerInvariant());

    public static readonly DiagnosticDescriptor InvalidCronSchedule = new(
        DiagnosticIds.InvalidCronSchedule,
        title: "A cron schedule that Inngest does not accept",
        messageFormat: "The cron schedule \"{0}\" is not valid: {1}",
        category: FunctionsCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Inngest reads a schedule with five fields, minute, hour, day of month, month and day of week, and no descriptors such as @daily, and refuses to sync the app when one of its functions has a schedule it cannot read. The SDK checks schedules the same way at startup.",
        helpLinkUri: GuideUrl + "#" + DiagnosticIds.InvalidCronSchedule.ToLowerInvariant());

    public static readonly DiagnosticDescriptor InvalidTimeString = new(
        DiagnosticIds.InvalidTimeString,
        title: "A time string in a function attribute is not valid",
        messageFormat: "{0} of [{1}] is \"{2}\", which {3}",
        category: FunctionsCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The attributes take durations as time strings: whole numbers followed by w, d, h, m or s, such as \"30s\" or \"1h30m\". The SDK fails at startup on a string it cannot read, and on a duration outside what Inngest allows for the setting, such as a rate limit period over 24 hours.",
        helpLinkUri: GuideUrl + "#" + DiagnosticIds.InvalidTimeString.ToLowerInvariant());

    public static readonly DiagnosticDescriptor RepeatedStepId = new(
        DiagnosticIds.RepeatedStepId,
        title: "Two steps of a function have the same id",
        messageFormat: "The step id \"{0}\" is also used by {1} on line {2}; give each step an id of its own",
        category: StepsCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Inngest saves a step's result under its id. The SDK numbers an id that is used again, in the order of the calls, which is right for a step called in a loop, but two different steps with one id are matched to their results by order alone: steps that run in parallel, or one that is skipped in a later call, get each other's results. Steps in different branches of an if or a switch are not reported.",
        helpLinkUri: GuideUrl + "#" + DiagnosticIds.RepeatedStepId.ToLowerInvariant());

    public static readonly DiagnosticDescriptor EventNameMismatch = new(
        DiagnosticIds.EventNameMismatch,
        title: "A function is triggered by an event other than the one its event data is for",
        messageFormat: "'{0}' reads its events as {1}, whose EventName is \"{2}\", but is triggered by \"{3}\"",
        category: FunctionsCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The type argument of InngestFunction<TEventData> says what the data of the function's events looks like, and a type that implements IInngestEventData names its event. A function that none of its event triggers runs for that event reads the data of other events as that type. Use [EventTrigger<T>], or the event data type of the event the function is for. It is reported only when no event trigger of the function matches.",
        helpLinkUri: GuideUrl + "#" + DiagnosticIds.EventNameMismatch.ToLowerInvariant());
}
