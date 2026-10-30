using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Inngest.Templates.Tests;

// These install the template package the way a user does, make projects with it and build them, so each takes a while.
// There are few of them for that reason: one web app that is also started, one worker for the other framework, and a
// look at what the package offers.
public sealed partial class TemplateTests(TemplateFixture fixture) : IClassFixture<TemplateFixture>
{
    [Fact]
    public async Task The_package_offers_the_two_templates_with_their_options()
    {
        var list = await DotnetCli.RunAsync(fixture.Workspace, ["new", "list", "--debug:custom-hive", fixture.Hive]);
        list.ExitCode.ShouldBe(0, list.Output);
        list.Output.ShouldContain("inngest-web");
        list.Output.ShouldContain("inngest-worker");

        var web = await DotnetCli.RunAsync(fixture.Workspace, ["new", "inngest-web", "--help", "--debug:custom-hive", fixture.Hive]);
        web.Output.ShouldContain("--framework");
        web.Output.ShouldContain("net10.0");
        web.Output.ShouldContain("net8.0");
        web.Output.ShouldContain("--http-port");
        web.Output.ShouldContain("--no-restore");

        var worker = await DotnetCli.RunAsync(fixture.Workspace, ["new", "inngest-worker", "--help", "--debug:custom-hive", fixture.Hive]);
        worker.Output.ShouldContain("--framework");
        worker.Output.ShouldNotContain("--http-port", Case.Sensitive, "a worker has no endpoint");
    }

    [Fact]
    public async Task The_framework_is_a_choice_between_the_two_the_sdk_supports()
    {
        var created = await fixture.NewAsync("inngest-web", "Unsupported.Framework", "-f", "net6.0", "--no-restore");

        created.ExitCode.ShouldNotBe(0);
        Directory.Exists(Path.Combine(fixture.Workspace, "Unsupported.Framework")).ShouldBeFalse();
    }

    [Fact]
    public async Task The_web_template_makes_an_app_that_builds_and_serves_Inngest_in_dev_mode()
    {
        var port = TemplateFixture.FreePort();
        var created = await fixture.NewAsync("inngest-web", "Acme.Orders", "--http-port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--no-restore");
        created.ExitCode.ShouldBe(0, created.Output);
        var project = Path.Combine(fixture.Workspace, "Acme.Orders");

        // What was made: named after the project, with the version of the template package in place of the token.
        var csproj = File.ReadAllText(Path.Combine(project, "Acme.Orders.csproj"));
        csproj.ShouldContain($"""<PackageReference Include="Inngest.AspNetCore" Version="{fixture.Version}" />""");
        csproj.ShouldContain("<TargetFramework>net10.0</TargetFramework>");
        UserSecretsId().Match(csproj).Success.ShouldBeTrue("the project has a user secrets id");
        csproj.ShouldNotContain("88c93898-0f2b-4b0a-8cb2-8f8fe6c5fed2", Case.Sensitive, "the id of the template is replaced by a new one");
        Directory.GetFiles(project, "*", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}.template.config{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ShouldAllBe(file => !File.ReadAllText(file).Contains("!!") && !File.ReadAllText(file).Contains("Inngest.Web"), "no token or name of the template is left");

        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(project, "appsettings.json")));
        settings.RootElement.GetProperty("Inngest").GetProperty("AppId").GetString().ShouldBe("acme-orders");
        using var development = JsonDocument.Parse(File.ReadAllText(Path.Combine(project, "appsettings.Development.json")));
        development.RootElement.GetProperty("Inngest").GetProperty("IsDev").GetBoolean().ShouldBeTrue();
        File.ReadAllText(Path.Combine(project, "Properties", "launchSettings.json")).ShouldContain($"http://localhost:{port}");

        // Warnings are errors, as a team that adopts the template may want them, but not those about advisories for
        // packages: an advisory that appears later would fail a test that did not change.
        var build = await DotnetCli.RunAsync(project, ["build", "-c", "Release", "-nologo", "-warnaserror", "-p:NuGetAudit=false"]);
        build.ExitCode.ShouldBe(0, build.Output);

        // The launch profile runs the app in Development, where the settings turn dev mode on and it needs no keys.
        using var app = DotnetCli.Start(project, ["run", "--no-build", "-c", "Release"]);
        using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(5) };
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
        string? body = null;
        while (body is null)
        {
            app.HasExited.ShouldBeFalse($"the app stopped:{Environment.NewLine}{app.Output}");
            timeout.IsCancellationRequested.ShouldBeFalse($"the app did not answer:{Environment.NewLine}{app.Output}");
            try
            {
                body = await http.GetStringAsync($"http://localhost:{port}/api/inngest", timeout.Token);
            }
            catch (HttpRequestException)
            {
                await Task.Delay(250, timeout.Token);
            }
        }

        using var introspection = JsonDocument.Parse(body);
        introspection.RootElement.GetProperty("mode").GetString().ShouldBe("dev");
        introspection.RootElement.GetProperty("function_count").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task The_worker_template_makes_a_worker_for_net8_that_builds()
    {
        var created = await fixture.NewAsync("inngest-worker", "Billing.Worker", "-f", "net8.0", "--no-restore");
        created.ExitCode.ShouldBe(0, created.Output);
        var project = Path.Combine(fixture.Workspace, "Billing.Worker");

        var csproj = File.ReadAllText(Path.Combine(project, "Billing.Worker.csproj"));
        csproj.ShouldContain("<TargetFramework>net8.0</TargetFramework>");
        csproj.ShouldContain($"""<PackageReference Include="Inngest.Connect" Version="{fixture.Version}" />""");
        csproj.ShouldContain($"""<PackageReference Include="Microsoft.Extensions.Hosting" Version="{CentralHostingVersion(fixture.RepositoryRoot)}" />""");
        csproj.ShouldNotContain("net10.0", Case.Sensitive, "the framework option replaces it everywhere");
        File.ReadAllText(Path.Combine(project, "appsettings.json")).ShouldContain("\"AppId\": \"billing-worker\"");
        File.ReadAllText(Path.Combine(project, "Program.cs")).ShouldContain("AddConnect()");

        var build = await DotnetCli.RunAsync(project, ["build", "-c", "Release", "-nologo", "-warnaserror", "-p:NuGetAudit=false"]);
        build.ExitCode.ShouldBe(0, build.Output);
        File.Exists(Path.Combine(project, "bin", "Release", "net8.0", "Billing.Worker.dll")).ShouldBeTrue();
    }

    // The worker template gets the version of Microsoft.Extensions.Hosting from the central package versions when the
    // package is packed, so that it is the one this repository's tests run with. The project that packs targets net10.0.
    private static string CentralHostingVersion(string repositoryRoot)
    {
        var versions = XDocument.Load(Path.Combine(repositoryRoot, "Directory.Packages.props"))
            .Descendants("PackageVersion")
            .Where(package => (string?)package.Attribute("Include") == "Microsoft.Extensions.Hosting")
            .Where(package => ((string?)package.Attribute("Condition")) is not { } condition || condition.Contains("net10.0", StringComparison.Ordinal))
            .Select(package => (string?)package.Attribute("Version"))
            .ToList();

        versions.Count.ShouldBe(1, "Directory.Packages.props names one version of Microsoft.Extensions.Hosting for net10.0");
        return versions[0]!;
    }

    [GeneratedRegex("""<UserSecretsId>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}</UserSecretsId>""")]
    private static partial Regex UserSecretsId();
}
