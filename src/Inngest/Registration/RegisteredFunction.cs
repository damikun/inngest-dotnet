// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Inngest.Functions;

namespace Inngest.Registration;

// A function as Inngest knows it: under its composite id, and with the trigger that only the app id makes
// possible filled in for a failure or cancel handler.
internal sealed record RegisteredFunction(string CompositeId, FunctionOptions Options)
{
    public static RegisteredFunction Create(FunctionDefinition definition, string appId)
    {
        var options = definition.Options;
        if (definition.FailureHandlerFor is { } failedFunctionId)
        {
            options = WithTrigger(options, SystemEvents.FunctionFailed, ComposeId(appId, failedFunctionId));
        }

        if (definition.CancelHandlerFor is { } cancelledFunctionId)
        {
            options = WithTrigger(options, SystemEvents.FunctionCancelled, ComposeId(appId, cancelledFunctionId));
        }

        return new RegisteredFunction(ComposeId(appId, options.Id), options);
    }

    private static FunctionOptions WithTrigger(FunctionOptions options, string eventName, string functionId)
    {
        var trigger = new EventTrigger(eventName) { If = $"event.data.function_id == '{EscapeLiteral(functionId)}'" };
        return options with { Triggers = [.. options.Triggers, trigger] };
    }

    // Inngest addresses a function as "<app id>-<function id>", and the fnId query parameter carries the same value.
    private static string ComposeId(string appId, string functionId) => $"{appId}-{functionId}";

    // The id ends up inside a single-quoted string of an expression.
    private static string EscapeLiteral(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");
}
