using System.Diagnostics;
using System.Text;

namespace Inngest.Templates.Tests;

// Runs the dotnet command line, which is what a person who uses the templates does, and keeps what it printed.
internal static class DotnetCli
{
    public static readonly TimeSpan Generous = TimeSpan.FromMinutes(8);

    public static async Task<CommandResult> RunAsync(
        string workingDirectory, IEnumerable<string> arguments, IReadOnlyDictionary<string, string?>? environment = null, TimeSpan? timeout = null)
    {
        using var process = Start(workingDirectory, arguments, environment);
        using CancellationTokenSource cancellation = new(timeout ?? Generous);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"dotnet {string.Join(' ', arguments)} did not finish in {timeout ?? Generous}.{Environment.NewLine}{process.Output}");
        }

        return new CommandResult(process.ExitCode, process.Output);
    }

    // For a program that is meant to keep running, such as the app the template made.
    public static RunningProcess Start(string workingDirectory, IEnumerable<string> arguments, IReadOnlyDictionary<string, string?>? environment = null)
    {
        ProcessStartInfo startInfo = new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // The commands run as they would for a person who has just installed the SDK, and not as the test run's own
        // settings have them: the dev server variables of a developer's shell would turn a mode on or off.
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        startInfo.Environment["MSBUILDTERMINALLOGGER"] = "off";
        foreach (var name in startInfo.Environment.Keys.Where(key => key.StartsWith("INNGEST_", StringComparison.Ordinal) && key != "INNGEST_BUILD_SLOT").ToList())
        {
            startInfo.Environment.Remove(name);
        }

        foreach (var (name, value) in environment ?? new Dictionary<string, string?>())
        {
            startInfo.Environment[name] = value;
        }

        var process = new RunningProcess(Process.Start(startInfo) ?? throw new InvalidOperationException("dotnet did not start."));
        return process;
    }
}

internal sealed record CommandResult(int ExitCode, string Output);

internal sealed class RunningProcess : IDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _output = new();

    public RunningProcess(Process process)
    {
        _process = process;
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    public bool HasExited => _process.HasExited;

    public int ExitCode => _process.ExitCode;

    public string Output
    {
        get
        {
            lock (_output)
            {
                return _output.ToString();
            }
        }
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken) => _process.WaitForExitAsync(cancellationToken);

    public void Kill(bool entireProcessTree)
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree);
        }
    }

    public void Dispose()
    {
        Kill(entireProcessTree: true);
        _process.Dispose();
    }

    private void Append(string? line)
    {
        if (line is not null)
        {
            lock (_output)
            {
                _output.AppendLine(line);
            }
        }
    }
}
