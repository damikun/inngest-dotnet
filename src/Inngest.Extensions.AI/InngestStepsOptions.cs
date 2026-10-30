// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

namespace Inngest;

/// <summary>How the steps that UseInngestSteps and UseInngestFunctionInvocation make of model calls and tool calls are recorded.</summary>
public sealed class InngestStepsOptions
{
    /// <summary>
    /// Whether a step records its input, which the Inngest UI shows and which a rerun with an edit passes on: the
    /// messages and options of a model call, the arguments of a tool call. Inngest stores the input as plain JSON, which
    /// the encryption middleware does not cover, and a long conversation repeats in the input of every model call of
    /// a run. Turn it off for such prompts: the step then runs as a plain run step. The default is true.
    /// </summary>
    public bool RecordInput { get; set; } = true;
}
