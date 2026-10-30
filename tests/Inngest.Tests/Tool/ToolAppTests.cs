using System.Runtime.InteropServices;
using Inngest.Tool;
using Inngest.Tool.Release;

namespace Inngest.Tests.Tool;

public sealed class ToolAppTests : IDisposable
{
    private const string Version = CliRelease.PinnedVersion;

    private readonly TempDirectory _cache = new();
    private readonly TempDirectory _project = new();
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();
    private readonly FakeReleaseHandler _release = new();
    private readonly List<(string File, string[] Arguments)> _runs = [];
    private int _exitCode;

    public ToolAppTests()
    {
        // The release of the pinned version, for the platform of the test, so that nothing depends on the machine.
        var archive = Archives.TarGz(("inngest", Archives.Binary));
        _release.Serve(CliRelease.AssetUri(Version, Asset(Version).Name).ToString(), archive);
        Checksums = ChecksumFile.Parse($"{Archives.Sha256(archive)}  {Asset(Version).Name}");
    }

    private ChecksumFile Checksums { get; }

    public void Dispose()
    {
        _cache.Dispose();
        _project.Dispose();
    }

    private static CliAsset Asset(string version) => CliAsset.Select(version, OSPlatform.Linux, Architecture.X64);

    private Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        CliInstaller installer = new(new HttpClient(_release), _cache.Path, _error, Version, Checksums);
        ToolApp app = new(_output, _error, installer, Asset, (file, arguments) =>
        {
            _runs.Add((file, [.. arguments]));
            return Task.FromResult(_exitCode);
        }, _project.Path);
        return app.RunAsync(args, cancellationToken);
    }

    private void LaunchSettings(string applicationUrl)
    {
        Directory.CreateDirectory(_project.Combine("Properties"));
        File.WriteAllText(
            _project.Combine("Properties", "launchSettings.json"),
            $$"""{ "profiles": { "http": { "commandName": "Project", "applicationUrl": "{{applicationUrl}}" } } }""");
    }

    [Fact]
    public async Task Dev_runs_the_pinned_cli_with_the_url_from_the_launch_settings()
    {
        LaunchSettings("http://localhost:5280");

        var exitCode = await RunAsync(["dev"], TestContext.Current.CancellationToken);

        exitCode.ShouldBe(0);
        var run = _runs.ShouldHaveSingleItem();
        run.File.ShouldBe(_cache.Combine(Version, "linux_amd64", "inngest"));
        run.Arguments.ShouldBe(["dev", "-u", "http://localhost:5280/api/inngest"]);
        _error.ToString().ShouldContain(
            $"The dev server will call http://localhost:5280/api/inngest, from the profile http of {Path.Combine("Properties", "launchSettings.json")}.");
    }

    [Fact]
    public async Task A_url_in_the_arguments_wins_over_the_launch_settings()
    {
        LaunchSettings("http://localhost:5280");

        await RunAsync(["dev", "-u", "http://localhost:7000/inngest", "--port", "9000"], TestContext.Current.CancellationToken);

        _runs.ShouldHaveSingleItem().Arguments.ShouldBe(["dev", "-u", "http://localhost:7000/inngest", "--port", "9000"]);
        _error.ToString().ShouldNotContain("launch settings");
    }

    [Fact]
    public async Task A_worker_without_discovery_gets_no_url()
    {
        LaunchSettings("http://localhost:5280");

        await RunAsync(["dev", "--no-discovery"], TestContext.Current.CancellationToken);

        _runs.ShouldHaveSingleItem().Arguments.ShouldBe(["dev", "--no-discovery"]);
    }

    [Fact]
    public async Task Without_launch_settings_the_dev_server_is_left_to_find_the_app_and_the_user_is_told_how_to_name_it()
    {
        await RunAsync(["dev", "--port", "9000"], TestContext.Current.CancellationToken);

        _runs.ShouldHaveSingleItem().Arguments.ShouldBe(["dev", "--port", "9000"]);
        _error.ToString().ShouldContain("-u <url>");
        _error.ToString().ShouldContain("--project");
    }

    [Fact]
    public async Task The_project_option_reads_the_launch_settings_of_another_folder()
    {
        LaunchSettings("http://localhost:5280");
        using TempDirectory elsewhere = new();
        var tool = ToolFor(elsewhere.Path);

        await tool.RunAsync(["dev", "--project", _project.Path], TestContext.Current.CancellationToken);

        _runs.ShouldHaveSingleItem().Arguments.ShouldBe(["dev", "-u", "http://localhost:5280/api/inngest"]);
    }

    [Fact]
    public async Task A_project_that_does_not_exist_is_an_error_before_anything_is_downloaded()
    {
        var exitCode = await RunAsync(["dev", "--project", _project.Combine("missing")], TestContext.Current.CancellationToken);

        exitCode.ShouldBe(1);
        _error.ToString().ShouldContain("is not a folder or a project file");
        _release.Requests.ShouldBeEmpty();
        _runs.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_exit_code_of_the_cli_is_the_exit_code_of_the_tool()
    {
        _exitCode = 7;

        (await RunAsync(["dev", "--no-discovery"], TestContext.Current.CancellationToken)).ShouldBe(7);
    }

    [Fact]
    public async Task Another_cli_version_is_downloaded_and_run_in_place_of_the_pinned_one()
    {
        var archive = Archives.TarGz(("inngest", Archives.Binary));
        _release.Serve(CliRelease.AssetUri("9.9.9", Asset("9.9.9").Name).ToString(), archive);
        _release.Serve(CliRelease.ChecksumsUri("9.9.9").ToString(), $"{Archives.Sha256(archive)}  {Asset("9.9.9").Name}\n");

        await RunAsync(["dev", "--cli-version", "v9.9.9", "--no-discovery"], TestContext.Current.CancellationToken);

        var run = _runs.ShouldHaveSingleItem();
        run.File.ShouldBe(_cache.Combine("9.9.9", "linux_amd64", "inngest"));
        run.Arguments.ShouldBe(["dev", "--no-discovery"], "--cli-version is not the CLI's");
    }

    [Fact]
    public async Task A_tampered_download_stops_the_tool_without_running_anything()
    {
        _release.Serve(CliRelease.AssetUri(Version, Asset(Version).Name).ToString(), Archives.TarGz(("inngest", "evil"u8.ToArray())));

        var exitCode = await RunAsync(["dev", "--no-discovery"], TestContext.Current.CancellationToken);

        exitCode.ShouldBe(1);
        _error.ToString().ShouldContain("error: ");
        _error.ToString().ShouldContain("does not match its SHA-256");
        _error.ToString().ShouldNotContain("docker");
        _runs.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failed_download_shows_the_docker_command_for_the_app()
    {
        LaunchSettings("http://localhost:5280");
        _release.Serve(CliRelease.AssetUri(Version, Asset(Version).Name).ToString(), [], System.Net.HttpStatusCode.BadGateway);

        var exitCode = await RunAsync(["dev"], TestContext.Current.CancellationToken);

        exitCode.ShouldBe(1);
        _error.ToString().ShouldContain(
            $"docker run --rm -p 8288:8288 -p 8289:8289 --add-host=host.docker.internal:host-gateway inngest/inngest:v{Version} inngest dev -u http://host.docker.internal:5280/api/inngest");
        _runs.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_docker_command_uses_the_url_that_was_given_and_the_version_that_was_asked_for()
    {
        _release.Serve(CliRelease.ChecksumsUri("9.9.8").ToString(), $"{new string('a', 64)}  {Asset("9.9.8").Name}\n");
        _release.Serve(CliRelease.AssetUri("9.9.8", Asset("9.9.8").Name).ToString(), [], System.Net.HttpStatusCode.BadGateway);

        var exitCode = await RunAsync(["dev", "--cli-version", "9.9.8", "-u", "http://localhost:4000/api/inngest"], TestContext.Current.CancellationToken);

        exitCode.ShouldBe(1);
        _error.ToString().ShouldContain("--add-host=host.docker.internal:host-gateway inngest/inngest:v9.9.8 inngest dev -u http://host.docker.internal:4000/api/inngest");

        // The same url in the other spellings.
        foreach (var option in new[] { "-u=http://localhost:4000/api/inngest", "--sdk-url=http://localhost:4000/api/inngest" })
        {
            _error.GetStringBuilder().Clear();
            await RunAsync(["dev", "--cli-version", "9.9.8", option], TestContext.Current.CancellationToken);
            _error.ToString().ShouldContain("inngest dev -u http://host.docker.internal:4000/api/inngest");
        }
    }

    [Fact]
    public async Task The_docker_command_for_a_worker_has_no_url_and_keeps_the_dev_server_from_looking_for_one()
    {
        _release.Serve(CliRelease.AssetUri(Version, Asset(Version).Name).ToString(), [], System.Net.HttpStatusCode.BadGateway);

        await RunAsync(["dev", "--no-discovery", "--port", "9000"], TestContext.Current.CancellationToken);

        _error.ToString().ShouldContain($"  docker run --rm -p 8288:8288 -p 8289:8289 inngest/inngest:v{Version} inngest dev --no-discovery{Environment.NewLine}");
    }

    [Fact]
    public async Task A_version_that_does_not_exist_is_no_reason_for_docker()
    {
        var exitCode = await RunAsync(["dev", "--cli-version", "9.9.7", "--no-discovery"], TestContext.Current.CancellationToken);

        exitCode.ShouldBe(1);
        _error.ToString().ShouldContain("does not exist");
        _error.ToString().ShouldNotContain("docker run");
    }

    [Fact]
    public async Task A_download_that_is_cancelled_ends_the_tool_with_the_exit_code_of_an_interrupt()
    {
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        var exitCode = await RunAsync(["dev", "--no-discovery"], cancellation.Token);

        exitCode.ShouldBe(130);
        _runs.ShouldBeEmpty();
    }

    [Fact]
    public async Task Help_goes_to_the_output_and_names_the_pinned_version_and_the_options()
    {
        var exitCode = await RunAsync(["--help"], TestContext.Current.CancellationToken);

        exitCode.ShouldBe(0);
        _output.ToString().ShouldContain($"{Version} unless --cli-version names another");
        _output.ToString().ShouldContain("--sdk-url");
        _output.ToString().ShouldContain("--cli-version");
        _output.ToString().ShouldContain("--project");
        _error.ToString().ShouldBeEmpty();
        _release.Requests.ShouldBeEmpty("help needs no download");
    }

    [Fact]
    public async Task The_version_names_the_tool_and_the_cli_it_runs()
    {
        var exitCode = await RunAsync(["--version"], TestContext.Current.CancellationToken);

        exitCode.ShouldBe(0);
        _output.ToString().ShouldMatch($@"^dotnet-inngest \d+\.\d+\.\d+\S* \(Inngest CLI {Version}\)\s*$");
    }

    [Fact]
    public async Task A_usage_error_goes_to_the_error_output_with_exit_code_2()
    {
        var exitCode = await RunAsync(["serve"], TestContext.Current.CancellationToken);

        exitCode.ShouldBe(2);
        _error.ToString().ShouldContain("error: Unknown command 'serve'.");
        _output.ToString().ShouldBeEmpty();
    }

    private ToolApp ToolFor(string currentDirectory)
    {
        CliInstaller installer = new(new HttpClient(_release), _cache.Path, _error, Version, Checksums);
        return new ToolApp(_output, _error, installer, Asset, (file, arguments) =>
        {
            _runs.Add((file, [.. arguments]));
            return Task.FromResult(_exitCode);
        }, currentDirectory);
    }
}
