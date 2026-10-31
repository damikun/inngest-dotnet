using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Inngest.Aspire.Hosting.Tests.AppModel;

// Versions that are written in more than one file, and that are wrong in all but one when only that one is raised.
public sealed partial class PinnedVersionsTests
{
    private static readonly string[] AppHostProjects =
    [
        "tests/Inngest.Aspire.Hosting.Tests/Inngest.Aspire.Hosting.Tests.csproj",
        "samples/Inngest.Sample.AppHost/Inngest.Sample.AppHost.csproj",
    ];

    // The AppHost SDK takes the orchestrator and the dashboard from the version of Aspire.Hosting.AppHost, and the package
    // is built against Aspire.Hosting; versions that differ give an AppHost that mixes releases.
    [Fact]
    public void The_AppHost_SDK_and_the_Aspire_packages_have_one_version()
    {
        using var globalJson = JsonDocument.Parse(Repository.ReadAllText("global.json"));
        var sdk = globalJson.RootElement.GetProperty("msbuild-sdks").GetProperty("Aspire.AppHost.Sdk").GetString();
        var packages = XDocument.Parse(Repository.ReadAllText("Directory.Packages.props")).Descendants("PackageVersion").ToList();

        foreach (var id in new[] { "Aspire.Hosting", "Aspire.Hosting.AppHost", "Aspire.Hosting.Testing" })
        {
            var version = packages.Single(package => (string?)package.Attribute("Include") == id).Attribute("Version")!.Value;
            version.ShouldBe(sdk, $"Directory.Packages.props has {id} {version}, and global.json has Aspire.AppHost.Sdk {sdk}.");
        }
    }

    [Fact]
    public void The_project_files_leave_the_version_of_the_AppHost_SDK_to_global_json()
    {
        foreach (var project in AppHostProjects)
        {
            var sdk = SdkAttribute().Match(Repository.ReadAllText(project));
            sdk.Success.ShouldBeTrue($"{project} does not name Aspire.AppHost.Sdk.");
            sdk.Groups[1].Value.ShouldBe("Aspire.AppHost.Sdk", $"{project} names a version of its own.");
        }
    }

    [Fact]
    public void The_default_image_is_the_one_the_integration_tests_of_the_repository_run()
    {
        var harness = Repository.ReadAllText("tests/Inngest.IntegrationTests/Harness/InngestServer.cs");
        var image = ImageConstant().Match(harness);
        image.Success.ShouldBeTrue("InngestServer.cs no longer has a constant named Image.");

        image.Groups[1].Value.ShouldBe($"{InngestContainerImageTags.Image}:{InngestContainerImageTags.Tag}");
    }

    [GeneratedRegex(@"<Project\s+Sdk=""(Aspire\.AppHost\.Sdk[^""]*)""")]
    private static partial Regex SdkAttribute();

    [GeneratedRegex(@"const\s+string\s+Image\s*=\s*""([^""]+)""")]
    private static partial Regex ImageConstant();
}
