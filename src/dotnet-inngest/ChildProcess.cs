// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Inngest.Tool;

internal static class ChildProcess
{
    // Starts the program on the console of this process, so that its output and its input are the user's, and waits for
    // it. The exit code is the program's.
    //
    // Ctrl+C and SIGTERM must not end the tool before the program, or it would be left running without a parent. In a
    // terminal Ctrl+C reaches the program as well, so passing it on delivers it twice, which the program takes as one.
    // A signal sent to the tool alone, by an IDE or a script, reaches it only this way. Windows has no signal for a
    // single process, and its console sends Ctrl+C to every process attached to it.
    //
    // What cannot be passed on, SIGKILL or a crash, and on Windows an end of the tool alone, leaves the program running.
    // A job object would end it with the tool on Windows, and is not here because it could not be tested.
    public static async Task<int> RunAsync(string fileName, IReadOnlyList<string> arguments)
    {
        ProcessStartInfo startInfo = new(fileName) { UseShellExecute = false };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new ToolException($"Could not start {fileName}.");
        }
        catch (Win32Exception exception)
        {
            throw new ToolException($"Could not start {fileName}: {exception.Message}", exception);
        }

        using (process)
        using (OperatingSystem.IsWindows() ? null : PassOn(PosixSignal.SIGINT, "INT", process))
        using (OperatingSystem.IsWindows() ? null : PassOn(PosixSignal.SIGTERM, "TERM", process))
        {
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
    }

    private static PosixSignalRegistration PassOn(PosixSignal signal, string name, Process process) =>
        PosixSignalRegistration.Create(signal, context =>
        {
            context.Cancel = true;
            Send(process, name);
        });

    private static void Send(Process process, string signal)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            // The program may end between the check and kill, and kill then complains about it, which is nothing the user
            // needs to see.
            ProcessStartInfo startInfo = new("kill") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            startInfo.ArgumentList.Add("-" + signal);
            startInfo.ArgumentList.Add(process.Id.ToString(CultureInfo.InvariantCulture));
            using var kill = Process.Start(startInfo);
            kill?.StandardError.ReadToEnd();
            kill?.WaitForExit();
        }
        catch (Win32Exception)
        {
            // A system without a kill program, such as a minimal container.
            process.Kill();
        }
        catch (InvalidOperationException)
        {
            // The program ended in the meantime.
        }
    }
}
