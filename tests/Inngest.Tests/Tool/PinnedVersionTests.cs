using Inngest.Tool.Download;

namespace Inngest.Tests.Tool;

public sealed class PinnedVersionTests
{
    // The CLI the tool runs, the server the integration tests run against and the one the docs start are one release, so
    // that what the tests proved is what a user runs. Raising one without the others fails here.
    [Fact]
    public void The_pinned_version_is_the_release_of_the_image_the_integration_tests_and_the_docs_use()
    {
        var root = RepositoryRoot();
        Assert.SkipWhen(root is null, "The sources of the repository are not next to the test run.");
        var image = $"inngest/inngest:v{CliRelease.PinnedVersion}";

        File.ReadAllText(Path.Combine(root, "tests", "Inngest.IntegrationTests", "Harness", "InngestServer.cs")).ShouldContain(image);
        File.ReadAllText(Path.Combine(root, "site", "src", "data", "dev-server.ts")).ShouldContain(image);
    }

    private static string? RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Inngest.slnx")))
            {
                return directory.FullName;
            }
        }

        return null;
    }
}
