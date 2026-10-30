// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Reflection;
using Inngest.Tool.Download;

namespace Inngest.Tool;

// Runs one command line: help, the version, or the dev server. The pieces that touch the machine are passed in, so that
// the tests can run it without a download or a process.
internal sealed class ToolApp(
    TextWriter output,
    TextWriter error,
    CliInstaller installer,
    Func<string, CliAsset> selectAsset,
    Func<string, IReadOnlyList<string>, Task<int>> run,
    string currentDirectory)
{
    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var arguments = Arguments.Parse(args);
        switch (arguments.Kind)
        {
            case CommandKind.Help:
                output.Write(Help.Text);
                return 0;
            case CommandKind.Version:
                output.WriteLine(VersionText());
                return 0;
            case CommandKind.Invalid:
                error.WriteLine($"error: {arguments.Error}");
                error.WriteLine("Run 'dotnet inngest --help' for the usage.");
                return 2;
        }

        try
        {
            return await RunDevAsync(arguments, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
    }

    private async Task<int> RunDevAsync(Arguments arguments, CancellationToken cancellationToken)
    {
        string? version = null;
        AppUrl? app = null;
        try
        {
            version = arguments.CliVersion ?? CliRelease.PinnedVersion;

            List<string> cliArguments = ["dev"];
            if (!arguments.NamesApp)
            {
                app = FindApp(arguments.Project);
                if (app is not null)
                {
                    cliArguments.AddRange(["-u", app.Url]);
                }
            }

            cliArguments.AddRange(arguments.Forwarded);

            var binary = await installer.EnsureInstalledAsync(version, selectAsset(version), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return await run(binary, cliArguments);
        }
        catch (ToolException exception)
        {
            error.WriteLine($"error: {exception.Message}");
            if (exception.SuggestsDocker)
            {
                error.WriteLine("The dev server also runs in Docker:");
                error.WriteLine($"  {DockerCommand(version ?? CliRelease.PinnedVersion, app, arguments)}");
            }

            return 1;
        }
    }

    // The address of the app is told to the user, since nothing in the command line says where it came from.
    private AppUrl? FindApp(string? project)
    {
        var path = project ?? currentDirectory;
        if (project is not null && !File.Exists(path) && !Directory.Exists(path))
        {
            throw new ToolException($"{project} is not a folder or a project file.");
        }

        var app = LaunchSettings.Find(path);
        error.WriteLine(app is null
            ? "No app address was found in the launch settings, so the dev server looks for apps on its own. -u <url> names one, "
              + "and --project <path> names the project whose Properties/launchSettings.json to read."
            : $"The dev server will call {app.Url}, from the profile {app.Profile} of {Path.GetRelativePath(currentDirectory, app.File)}. "
              + "-u <url> names another.");
        return app;
    }

    // The container reaches the app on the host, where localhost would be the container itself. Only what decides which
    // app the dev server finds is carried over: the options for ports and files mean something else in a container.
    private static string DockerCommand(string version, AppUrl? app, Arguments arguments)
    {
        var url = app?.Url ?? UrlOf(arguments.Forwarded);

        // Docker Engine on Linux, unlike Docker Desktop, does not know host.docker.internal unless it is told.
        var host = url is null ? "" : " --add-host=host.docker.internal:host-gateway";
        var target = url is null ? "" : " -u " + url.Replace("://localhost", "://host.docker.internal", StringComparison.Ordinal);
        var discovery = arguments.Forwarded.Contains("--no-discovery") ? " --no-discovery" : "";
        return $"docker run --rm -p 8288:8288 -p 8289:8289{host} inngest/inngest:v{version} inngest dev{target}{discovery}";
    }

    private static string? UrlOf(IReadOnlyList<string> arguments)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] is "-u" or "--sdk-url")
            {
                return i + 1 < arguments.Count ? arguments[i + 1] : null;
            }

            foreach (var prefix in new[] { "-u=", "--sdk-url=" })
            {
                if (arguments[i].StartsWith(prefix, StringComparison.Ordinal))
                {
                    return arguments[i][prefix.Length..];
                }
            }
        }

        return null;
    }

    private static string VersionText()
    {
        var version = typeof(ToolApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

        // Source Link appends the commit to the version of a build.
        var commit = version.IndexOf('+', StringComparison.Ordinal);
        return $"dotnet-inngest {(commit >= 0 ? version[..commit] : version)} (Inngest CLI {CliRelease.PinnedVersion})";
    }
}
