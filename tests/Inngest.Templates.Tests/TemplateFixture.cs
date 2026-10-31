using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Inngest.Templates.Tests;

// Everything the tests share, made once: the packages of this repository packed into a folder that acts as a NuGet
// feed, the template package among them, and a template hive with that package installed. The hive is not the user's
// (--debug:custom-hive), so nothing the tests do shows in the templates installed on the machine.
public sealed class TemplateFixture : IAsyncLifetime
{
    // Every package the templates reference, with the projects they bring in.
    private static readonly string[] Projects = ["Inngest", "Inngest.AspNetCore", "Inngest.Connect", "Inngest.Templates", "Inngest.Webhooks"];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "inngest-template-tests-" + Guid.NewGuid().ToString("N"));

    // The restore keeps a package in the global packages folder by its id and version. A version of its own for every
    // run keeps a package from an earlier run, packed from other code, from being used in place of this one.
    public string Version { get; } = "0.0.0-templatetests." + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);

    public string RepositoryRoot { get; } = FindRepositoryRoot();

    public string Hive => Path.Combine(_root, "hive");

    public string Feed => Path.Combine(_root, "feed");

    // A folder for the projects of one test. Its nuget.config above them restores the Inngest packages from the feed
    // and everything else from nuget.org.
    public string Workspace => Path.Combine(_root, "work");

    // Whether the machine has a .NET 8 SDK. The commands run in a folder outside the repository, where the newest SDK
    // is used, so a project for net8.0 is built with the SDK it is meant for only if a global.json asks for it.
    public bool HasNet8Sdk { get; private set; }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Feed);
        Directory.CreateDirectory(Workspace);
        File.WriteAllText(Path.Combine(Workspace, "nuget.config"), NuGetConfig());

        // The packs get a build folder of their own, so that they leave alone the build the test run itself is in.
        var slot = Environment.GetEnvironmentVariable("INNGEST_BUILD_SLOT");
        Dictionary<string, string?> environment = new() { ["INNGEST_BUILD_SLOT"] = slot is { Length: > 0 } ? slot + "-templates" : "templates" };
        foreach (var project in Projects)
        {
            // Package validation compares with the last release on nuget.org, which is not what is under test.
            var pack = await DotnetCli.RunAsync(
                RepositoryRoot,
                ["pack", Path.Combine("src", project, project + ".csproj"), "-c", "Release", "-o", Feed, "-nologo", $"-p:Version={Version}", "-p:EnablePackageValidation=false"],
                environment);
            pack.ExitCode.ShouldBe(0, $"packing {project}:{Environment.NewLine}{pack.Output}");
        }

        var install = await DotnetCli.RunAsync(
            Workspace, ["new", "install", Path.Combine(Feed, $"Inngest.Templates.{Version}.nupkg"), "--debug:custom-hive", Hive]);
        install.ExitCode.ShouldBe(0, install.Output);

        var sdks = await DotnetCli.RunAsync(Workspace, ["--list-sdks"]);
        HasNet8Sdk = sdks.ExitCode == 0 && sdks.Output.Split('\n').Any(line => line.StartsWith("8.0.", StringComparison.Ordinal));
    }

    public async ValueTask DisposeAsync()
    {
        // The packages of the run in the global packages folder are of no use to anyone after it.
        var locals = await DotnetCli.RunAsync(Workspace, ["nuget", "locals", "global-packages", "--list"]);
        if (locals.ExitCode == 0 && locals.Output.Split("global-packages:", 2) is [_, var path])
        {
            foreach (var project in Projects)
            {
                TryDelete(Path.Combine(path.Trim(), project.ToLowerInvariant(), Version));
            }
        }

        TryDelete(_root);
    }

    internal Task<CommandResult> NewAsync(string template, string name, params string[] options) =>
        DotnetCli.RunAsync(
            Workspace,
            ["new", template, "-n", name, "-o", Path.Combine(Workspace, name), "--debug:custom-hive", Hive, .. options]);

    public static int FreePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private string NuGetConfig() => new StringBuilder()
        .AppendLine("""<?xml version="1.0" encoding="utf-8"?>""")
        .AppendLine("<configuration>")
        .AppendLine("  <packageSources>")
        .AppendLine("    <clear />")
        .AppendLine($"""    <add key="feed" value="{Feed}" />""")
        .AppendLine("""    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />""")
        .AppendLine("  </packageSources>")
        .AppendLine("  <packageSourceMapping>")
        .AppendLine("""    <packageSource key="feed"><package pattern="Inngest*" /></packageSource>""")
        .AppendLine("""    <packageSource key="nuget.org"><package pattern="*" /></packageSource>""")
        .AppendLine("  </packageSourceMapping>")
        .AppendLine("</configuration>")
        .ToString();

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Inngest.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Inngest.slnx was not found above " + AppContext.BaseDirectory);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
