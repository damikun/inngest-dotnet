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
        // when it invokes them concurrently. Conversations that run side by side would claim the same ids in no fixed order,
        // so a conversation the caller named carries its name into the ids of its tools.
        var conversation = context.Options is { } chatOptions ? CallSettings.Of(chatOptions).StepId : null;
        var stepId = conversation is null ? $"tool {context.Function.Name}" : $"{conversation} tool {context.Function.Name}";

        var serialized = ToolJson.TryWriteInput(context, out var problem);
        if (problem is not null && options.RecordInput)
        {
            // The function's logger, which drops what is written while the run replays, so that this is written once.
            Log.InputNotRecorded(Invocation.AmbientContext?.Logger ?? NullLogger.Instance, stepId, problem);
        }

        var input = options.RecordInput ? serialized?.Json : null;

        async Task<JsonElement> HandleAsync(JsonElement received, CancellationToken stepToken)
        {
            if (input is { } recorded && ChatJson.IsEdited(received, recorded))
            {
                context.Arguments = ToolJson.ReadArguments(received, context.Arguments);
            }

            StepFunctionInvokingChatClient.Enter(context);
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

            try
            {
                return ToolJson.WriteResult(result, context.Terminate, serialized?.Digest, context.Function.JsonSerializerOptions);
            }
            catch (Exception exception) when (exception is NotSupportedException or JsonException)
            {
                // The tool has run, and a retry would run it again for a result that cannot be saved either.
                throw new NonRetriableException($"The result of tool {context.Function.Name} could not be written as JSON: {exception.Message}", exception);
            }
        }

        var stored = input is { } stepInput
            ? await steps.AIWrapAsync(stepId, stepInput, HandleAsync).ConfigureAwait(false)
            : await steps.RunAsync(stepId, stepToken => HandleAsync(default, stepToken)).ConfigureAwait(false);
        var (value, terminate, savedDigest) = ToolJson.ReadResult(stored);
        InputChecks.Check(stepId, savedDigest, serialized?.Digest);
        if (terminate)
        {
            context.Terminate = true;
        }

        return value;
    }
}
