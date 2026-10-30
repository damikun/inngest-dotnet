// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Runtime.CompilerServices;
using Inngest.Execution;

namespace Inngest.UserContext;

// The user of every run in progress, kept beside the run's context and not in a DI scope. A scoped step runs in a scope
// of its own and a service may be a singleton, and all of them have to see the user the function sees. So the current
// user is looked up from the async flow of the call, the way IHttpContextAccessor finds the request.
internal static class RunUsers
{
    private static readonly ConditionalWeakTable<FunctionContext, RunUser> Users = new();

    // Called when a function starts, on every call of a run.
    public static void Set(FunctionContext run, UserSnapshot? snapshot) => Users.AddOrUpdate(run, new RunUser(snapshot));

    // The user of the run whose code is running, and nobody where no run's code is. A fresh anonymous principal each
    // time, because a principal can be changed and nobody's must not carry what one caller added.
    public static RunUser Current => Invocation.AmbientContext is { } run ? Of(run) : new RunUser(null);

    // Nobody until the function has started, which is when the middleware sets the user.
    public static RunUser Of(FunctionContext run) => Users.GetValue(run, static _ => new RunUser(null));
}
