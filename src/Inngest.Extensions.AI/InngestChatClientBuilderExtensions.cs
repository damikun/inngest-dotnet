// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Inngest;
using Inngest.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Microsoft.Extensions.AI;

/// <summary>Makes the model calls and tool calls of a chat pipeline steps of the Inngest run they are made in.</summary>
public static class InngestChatClientBuilderExtensions
{
    /// <summary>
    /// Adds a stage that makes each call to the model inside a run a step: the model is called once, the complete response
    /// is saved with the run, and a replay returns it without calling the model, so a retry of a later step never pays for
    /// the same completion twice. The messages and options are saved as the step's input, which the Inngest UI shows and
    /// which a rerun with an edit passes to the model. A streaming call runs the whole stream in its step and then hands out
    /// the saved response as updates. Outside a run, and inside a step's handler, where steps cannot be nested, calls pass
    /// through. Add it next to the model, below <see cref="InngestChatClientBuilderExtensions.UseInngestFunctionInvocation"/>
    /// or UseFunctionInvocation, which call it once for each round of tool calls, and above stages that should only see
    /// the calls that really reach the model, such as UseOpenTelemetry and UseLogging.
    /// </summary>
    /// <param name="builder">The pipeline to add the stage to.</param>
    /// <param name="configure">Changes how the steps are recorded.</param>
    public static ChatClientBuilder UseInngestSteps(this ChatClientBuilder builder, Action<InngestStepsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Use((innerClient, services) =>
        {
            var options = new InngestStepsOptions();
            configure?.Invoke(options);
            return new InngestStepsChatClient(innerClient, options, LoggerOf(services));
        });
    }

    /// <summary>
    /// Adds a FunctionInvokingChatClient, as UseFunctionInvocation does, whose invocations of tools inside a run are steps:
    /// a tool runs once, its result is saved with the run, and a replay returns it without running the tool again. The
    /// arguments are the step's input. Outside a run, and inside a step's handler, tools are invoked as usual. A tool that
    /// throws is retried like any step, except for an ArgumentException, which fails the step at once; when the retries are
    /// used up, the client reports the failure to the model as it does for any exception of a tool. A FunctionInvoker set in
    /// <paramref name="configure"/> still invokes the tool, inside the step.
    /// </summary>
    /// <param name="builder">The pipeline to add the stage to.</param>
    /// <param name="configure">Changes the function invoking client, as the callback of UseFunctionInvocation does.</param>
    /// <param name="configureSteps">Changes how the steps are recorded.</param>
    public static ChatClientBuilder UseInngestFunctionInvocation(
        this ChatClientBuilder builder,
        Action<FunctionInvokingChatClient>? configure = null,
        Action<InngestStepsOptions>? configureSteps = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Use((innerClient, services) =>
        {
            var client = new FunctionInvokingChatClient(innerClient, services.GetService<ILoggerFactory>(), services);
            configure?.Invoke(client);
            var options = new InngestStepsOptions();
            configureSteps?.Invoke(options);
            client.FunctionInvoker = ToolSteps.Wrap(client.FunctionInvoker, options, LoggerOf(services));
            return client;
        });
    }

    private static ILogger LoggerOf(IServiceProvider services) =>
        services.GetService<ILoggerFactory>()?.CreateLogger<InngestStepsChatClient>() ?? NullLogger<InngestStepsChatClient>.Instance;
}
