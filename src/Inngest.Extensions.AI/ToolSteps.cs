// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Text.Json;
using Inngest.Execution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Inngest.Extensions.AI;

// Inside a run, the invocation of a tool by FunctionInvokingChatClient is a step: the tool runs once, its result is
// memoized, and a replay gets the result back without running the tool again. Everywhere else, outside a run and inside a
// step's handler, where steps cannot be nested, the tool is invoked as it is.
internal static class ToolSteps
{
    // Wraps what FunctionInvokingChatClient.FunctionInvoker does now, so that an invoker the app set stays in charge of how a
    // tool is called, and the step only memoizes what it returned.
    public static Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> Wrap(
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>? invoke,
        InngestStepsOptions options)
    {
        invoke ??= static (context, cancellationToken) => context.Function.InvokeAsync(context.Arguments, cancellationToken);
        return (context, cancellationToken) => Invocation.AmbientSteps is { } steps
            ? InvokeAsStepAsync(steps, invoke, context, options, cancellationToken)
            : invoke(context, cancellationToken);
    }

    private static async ValueTask<object?> InvokeAsStepAsync(
        IStepTools steps,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> invoke,
        FunctionInvocationContext context,
        InngestStepsOptions options,
        CancellationToken cancellationToken)
    {
        // The name of the tool, not of the call: the same tool called twice gets "tool get_weather" and "tool get_weather:1",
        // in the order FunctionInvokingChatClient makes the calls, which it makes in the order of the model's response, also
        // when it invokes them concurrently.
        var stepId = $"tool {context.Function.Name}";

        JsonElement? input = null;
        if (options.RecordInput)
        {
            input = ToolJson.TryWriteInput(context, out var problem);
            if (problem is not null)
            {
                // The function's logger, which drops what is written while the run replays, so that this is written once.
                Log.InputNotRecorded(Invocation.AmbientContext?.Logger ?? NullLogger.Instance, stepId, problem);
            }
        }

        async Task<JsonElement> HandleAsync(JsonElement received, CancellationToken stepToken)
        {
            if (input is { } recorded && ChatJson.IsEdited(received, recorded))
            {
                context.Arguments = ToolJson.ReadArguments(received, context.Arguments);
            }

            using var linked = cancellationToken.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(stepToken, cancellationToken) : null;
            object? result;
            try
            {
                result = await invoke(context, linked?.Token ?? stepToken).ConfigureAwait(false);
            }
            catch (ArgumentException exception)
            {
                // The model chose the arguments, and the same ones fail the same way again, so the retries Inngest would make
                // only delay telling the model.
                throw new NonRetriableException(exception.Message, exception);
            }

            return ToolJson.WriteResult(result, context.Terminate, context.Function.JsonSerializerOptions);
        }

        var stored = input is { } stepInput
            ? await steps.AIWrapAsync(stepId, stepInput, HandleAsync).ConfigureAwait(false)
            : await steps.RunAsync(stepId, stepToken => HandleAsync(default, stepToken)).ConfigureAwait(false);
        var (value, terminate) = ToolJson.ReadResult(stored);
        if (terminate)
        {
            context.Terminate = true;
        }

        return value;
    }
}
