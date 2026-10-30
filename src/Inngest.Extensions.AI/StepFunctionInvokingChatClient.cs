// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Inngest.Extensions.AI;

// A FunctionInvokingChatClient whose tools may run in the handler of a step. It sets FunctionInvokingChatClient.CurrentContext
// in the function's async flow, which a handler does not share, and the setter is only open to a derived class. A tool reads
// the context there to ask the client to stop calling the model.
internal sealed class StepFunctionInvokingChatClient(IChatClient innerClient, ILoggerFactory? loggerFactory, IServiceProvider? services)
    : FunctionInvokingChatClient(innerClient, loggerFactory, services)
{
    public static void Enter(FunctionInvocationContext context) => CurrentContext = context;
}
