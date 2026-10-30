// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Inngest.Tool.Release;

namespace Inngest.Tool;

internal enum CommandKind
{
    Dev,
    Help,
    Version,
    Invalid,
}

// The command line of the tool. A hand-written parser, not a library, since the tool is a thin launcher. It owns three
// options, and everything else belongs to 'inngest dev', whose options change with the release that is run, so it passes
// them on in order without knowing them.
internal sealed class Arguments
{
    private const string CliVersionOption = "--cli-version";
    private const string ProjectOption = "--project";

    public required CommandKind Kind { get; init; }

    public string? Error { get; init; }

    public string? CliVersion { get; init; }

    public string? Project { get; init; }

    // The arguments for 'inngest dev', in the order they were given.
    public IReadOnlyList<string> Forwarded { get; init; } = [];

    // Whether the arguments already say which app the dev server looks for, so that the tool adds none of its own.
    public bool NamesApp { get; init; }

    public static Arguments Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] is "-h" or "--help" or "-?" or "help")
        {
            return new Arguments { Kind = CommandKind.Help };
        }

        if (args[0] == "--version")
        {
            return new Arguments { Kind = CommandKind.Version };
        }

        if (args[0] != "dev")
        {
            return Invalid($"Unknown command '{args[0]}'.");
        }

        // The tool's own options end at --, so that an option of the Inngest CLI that shares a name can still be passed.
        var end = args.ToList().IndexOf("--");
        var own = end < 0 ? args.Count : end;

        string? cliVersion = null;
        string? project = null;
        List<string> forwarded = [];
        for (var i = 1; i < own; i++)
        {
            var argument = args[i];
            if (argument is "-h" or "--help")
            {
                return new Arguments { Kind = CommandKind.Help };
            }

            if (TryReadOption(CliVersionOption, args, own, ref i, out var value))
            {
                if (value is null)
                {
                    return Invalid($"{CliVersionOption} needs a version.");
                }

                cliVersion = value;
            }
            else if (TryReadOption(ProjectOption, args, own, ref i, out value))
            {
                if (value is null)
                {
                    return Invalid($"{ProjectOption} needs a folder or a project file.");
                }

                project = value;
            }
            else
            {
                forwarded.Add(argument);
            }
        }

        if (end >= 0)
        {
            forwarded.AddRange(args.Skip(end + 1));
        }

        if (cliVersion is not null)
        {
            if (!CliRelease.TryNormalizeVersion(cliVersion, out var version))
            {
                return Invalid($"'{cliVersion}' is not a release version of the Inngest CLI such as 1.49.0.");
            }

            cliVersion = version;
        }

        return new Arguments
        {
            Kind = CommandKind.Dev,
            CliVersion = cliVersion,
            Project = project,
            Forwarded = forwarded,
            NamesApp = forwarded.Any(NamesAnApp),
        };
    }

    private static Arguments Invalid(string error) => new() { Kind = CommandKind.Invalid, Error = error };

    // -u and --sdk-url name an app, and --no-discovery says there is none to look for, as for a Connect worker.
    private static bool NamesAnApp(string argument) =>
        argument is "-u" or "--sdk-url" or "--no-discovery"
        || argument.StartsWith("-u=", StringComparison.Ordinal)
        || argument.StartsWith("--sdk-url=", StringComparison.Ordinal)
        || argument.StartsWith("--no-discovery=", StringComparison.Ordinal);

    // Reads "--name value" or "--name=value". The value is null when the option is the last argument.
    private static bool TryReadOption(string name, IReadOnlyList<string> args, int end, ref int index, out string? value)
    {
        value = null;
        var argument = args[index];
        if (argument == name)
        {
            if (index + 1 < end)
            {
                value = args[++index];
            }

            return true;
        }

        if (argument.StartsWith(name + "=", StringComparison.Ordinal))
        {
            value = argument[(name.Length + 1)..];
            return true;
        }

        return false;
    }
}
