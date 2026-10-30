// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest.Functions;

// Collects what is wrong with the functions so that startup reports all of it in one exception. Otherwise three
// mistakes would take three restarts to find.
internal sealed class ConfigurationProblems
{
    private readonly List<string> _problems = [];

    public void Add(string problem) => _problems.Add(problem);

    public void Add(FunctionOptions function, string problem) => _problems.Add($"Function \"{function.Id}\": {problem}");

    // A single problem keeps its own message, so the common case reads as it always has.
    public void ThrowIfAny()
    {
        if (_problems.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(_problems.Count == 1 ? _problems[0] : Describe());
    }

    private string Describe() =>
        $"The function configuration has {_problems.Count} problems:{Environment.NewLine}" +
        string.Join(Environment.NewLine, _problems.Select(static (problem, index) => $"{index + 1}. {problem}"));
}
