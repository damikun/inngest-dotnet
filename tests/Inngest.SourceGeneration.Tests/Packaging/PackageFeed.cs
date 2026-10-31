namespace Inngest.SourceGeneration.Tests.Packaging;

// Inngest and Inngest.AspNetCore, packed from the source of the repository into a folder of their own. The pack builds into a
// folder of its own as well, so that it never touches the outputs of the build that the tests run beside, and it has a
// version no release has, so that nothing in the NuGet cache of the machine stands in for it.
public sealed class PackageFeed : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inngest-packages-" + Guid.NewGuid().ToString("N"));

    public string Version { get; } = "0.0.0-packagetest." + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);

    public string Folder => Path.Combine(_root, "feed");

    public string ConsumersFolder => Path.Combine(_root, "consumers");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Folder);
        Directory.CreateDirectory(ConsumersFolder);

        foreach (var project in new[] { "Inngest", "Inngest.AspNetCore" })
        {
            var (exitCode, output) = await DotnetCli.RunAsync(
                DotnetCli.RepositoryRoot,
                "pack",
                Path.Combine(DotnetCli.RepositoryRoot, "src", project, project + ".csproj"),
                "-c", "Release",
                "-o", Folder,
                "-p:Version=" + Version,
                "-p:ArtifactsPath=" + Path.Combine(_root, "artifacts"),
                "-p:EnablePackageValidation=false",
                "--nologo");
            exitCode.ShouldBe(0, "Packing " + project + " failed:\n" + output);
        }
    }

    public ValueTask DisposeAsync()
    {
        TryDelete(_root);

        // The consumers restored the packages into the NuGet cache of the machine.
        var packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        foreach (var id in new[] { "inngest", "inngest.aspnetcore" })
        {
            TryDelete(Path.Combine(packages, id, Version));
        }

        return ValueTask.CompletedTask;
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temporary folder that is left behind does no harm.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
