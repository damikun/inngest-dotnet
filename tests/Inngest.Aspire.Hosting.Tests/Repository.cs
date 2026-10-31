using System.Reflection;

namespace Inngest.Aspire.Hosting.Tests;

// The files of the repository, for the tests that compare what several files say.
internal static class Repository
{
    private static readonly string Root = FindRoot();

    public static string ReadAllText(string relativePath) =>
        File.ReadAllText(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    // The AppHost SDK records the directory of the project in the assembly, wherever the build writes its output.
    private static string FindRoot()
    {
        var start = typeof(Repository).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "apphostprojectpath")?.Value
            ?? AppContext.BaseDirectory;

        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json")) && File.Exists(Path.Combine(directory.FullName, "Inngest.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"The root of the repository is not above {start}.");
    }
}
