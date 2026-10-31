using System.Formats.Tar;
using System.Net;
using System.Runtime.InteropServices;
using Inngest.Tool;
using Inngest.Tool.Download;
using Microsoft.Extensions.Time.Testing;

namespace Inngest.Tests.Tool;

public sealed class CliInstallerTests : IDisposable
{
    private const string Version = "9.9.9";

    private readonly TempDirectory _cache = new();
    private readonly StringWriter _log = new();
    private readonly FakeReleaseHandler _release = new();

    public void Dispose() => _cache.Dispose();

    private static CliAsset Linux(string version = Version) => CliAsset.Select(version, OSPlatform.Linux, Architecture.X64);

    private static string Url(string file, string version = Version) => $"https://github.com/inngest/inngest/releases/download/v{version}/{file}";

    // As in the releases: the license, 30,630 bytes of it, and then the binary.
    private static byte[] LinuxArchive() => Archives.TarGz(("LICENSE.md", new byte[30_630]), ("inngest", Archives.Binary));

    // A release this tool carries the checksums of, as the pinned one is, with an asset for Linux.
    private CliInstaller Installer(byte[] archive)
    {
        _release.Serve(Url(Linux().Name), archive);
        var checksums = ChecksumFile.Parse($"{Archives.Sha256(archive)}  {Linux().Name}");
        return new CliInstaller(new HttpClient(_release), _cache.Path, _log, Version, checksums);
    }

    private string CliFolder(string version = Version) => _cache.Combine(version, "linux_amd64");

    [Fact]
    public async Task A_download_that_matches_the_pinned_checksum_is_unpacked_into_the_cache()
    {
        var installer = Installer(LinuxArchive());

        var binary = await installer.EnsureInstalledAsync(Version, Linux(), TestContext.Current.CancellationToken);

        binary.ShouldBe(Path.Combine(CliFolder(), "inngest"));
        File.ReadAllBytes(binary).ShouldBe(Archives.Binary);
        _release.Requests.ShouldBe([Url(Linux().Name)], "the pinned checksums need no request");
        _log.ToString().ShouldContain("Downloading inngest_9.9.9_linux_amd64.tar.gz");
    }

    [Fact]
    public async Task The_binary_can_be_run_although_the_archive_does_not_say_so()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has no execute bit.");
        var installer = Installer(LinuxArchive());

        var binary = await installer.EnsureInstalledAsync(Version, Linux(), TestContext.Current.CancellationToken);

        var mode = OperatingSystem.IsWindows() ? default : File.GetUnixFileMode(binary);
        mode.HasFlag(UnixFileMode.UserExecute).ShouldBeTrue();
        mode.HasFlag(UnixFileMode.GroupExecute).ShouldBeTrue();
        mode.HasFlag(UnixFileMode.OtherExecute).ShouldBeTrue();
    }

    [Fact]
    public async Task Only_the_binary_is_unpacked()
    {
        var installer = Installer(LinuxArchive());

        await installer.EnsureInstalledAsync(Version, Linux(), TestContext.Current.CancellationToken);

        Directory.GetFileSystemEntries(CliFolder()).Select(Path.GetFileName).ShouldBe(["inngest"]);
        Directory.GetFileSystemEntries(Path.Combine(_cache.Path, Version)).Select(Path.GetFileName).ShouldBe(["linux_amd64"], "no staging folder is left");
    }

    [Fact]
    public async Task A_release_that_is_installed_is_used_again_without_a_request()
    {
        var installer = Installer(LinuxArchive());
        var first = await installer.EnsureInstalledAsync(Version, Linux(), TestContext.Current.CancellationToken);
        var requests = _release.Requests.Count;

        var second = await installer.EnsureInstalledAsync(Version, Linux(), TestContext.Current.CancellationToken);

        second.ShouldBe(first);
        _release.Requests.Count.ShouldBe(requests);
    }

    [Fact]
    public async Task The_cache_is_kept_per_version_and_per_system()
    {
        var installer = Installer(LinuxArchive());
        await installer.EnsureInstalledAsync(Version, Linux(), TestContext.Current.CancellationToken);

        // Another system, or another version, is not served by what is there.
        var windows = CliAsset.Select(Version, OSPlatform.Windows, Architecture.X64);
        var zip = Archives.Zip(("inngest.exe", Archives.Binary));
        _release.Serve(Url(windows.Name), zip);
        var both = ChecksumFile.Parse($"{Archives.Sha256(zip)}  {windows.Name}");
        var windowsInstaller = new CliInstaller(new HttpClient(_release), _cache.Path, _log, Version, both);

        var binary = await windowsInstaller.EnsureInstalledAsync(Version, windows, TestContext.Current.CancellationToken);

        binary.ShouldBe(_cache.Combine(Version, "windows_amd64", "inngest.exe"));
        File.Exists(Path.Combine(CliFolder(), "inngest")).ShouldBeTrue();
    }

    [Fact]
    public async Task A_zip_archive_for_windows_is_unpacked_too()
    {
        var windows = CliAsset.Select(Version, OSPlatform.Windows, Architecture.Arm64);
        var zip = Archives.Zip(("LICENSE.md", "license"u8.ToArray()), ("inngest.exe", Archives.Binary));
        _release.Serve(Url(windows.Name), zip);
        var installer = new CliInstaller(
            new HttpClient(_release), _cache.Path, _log, Version, ChecksumFile.Parse($"{Archives.Sha256(zip)}  {windows.Name}"));

        var binary = await installer.EnsureInstalledAsync(Version, windows, TestContext.Current.CancellationToken);

        binary.ShouldBe(_cache.Combine(Version, "windows_arm64", "inngest.exe"));
        File.ReadAllBytes(binary).ShouldBe(Archives.Binary);
    }

    [Fact]
    public async Task A_download_that_differs_from_the_pinned_checksum_is_refused_and_leaves_nothing_behind()
    {
        // The release holds another file than the one the checksums were taken from.
        var original = LinuxArchive();
        var installer = Installer(original);
        _release.Serve(Url(Linux().Name), Archives.TarGz(("inngest", "#!/bin/sh\necho malware\n"u8.ToArray())));

        var exception = await Should.ThrowAsync<ToolException>(
            () => installer.EnsureInstalledAsync(Version, Linux(), TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("does not match its SHA-256");
        exception.Message.ShouldContain(Archives.Sha256(original));
        exception.SuggestsDocker.ShouldBeFalse("a replaced file is no reason to try another source quietly");
        Directory.Exists(CliFolder()).ShouldBeFalse();
        Directory.GetFileSystemEntries(Path.Combine(_cache.Path, Version)).ShouldBeEmpty("the staging folder is removed too");
    }

    [Fact]
    public async Task The_pinned_checksums_are_not_replaced_by_the_checksums_next_to_the_download()
    {
        // An attacker who replaces the file replaces checksums.txt as well. The pinned checksums must still win.
        var installer = Installer(LinuxArchive());
        var replacement = Archives.TarGz(("inngest", "#!/bin/sh\necho malware\n"u8.ToArray()));
        _release.Serve(Url(Linux().Name), replacement);
        _release.Serve(Url("checksums.txt"), $"{Archives.Sha256(replacement)}  {Linux().Name}\n");

        await Should.ThrowAsync<ToolException>(() => installer.EnsureInstalledAsync(Version, Linux(), TestContext.Current.CancellationToken));

        _release.Requests.ShouldNotContain(Url("checksums.txt"));
    }

    [Fact]
    public async Task Another_version_is_checked_against_the_checksums_of_its_release()
    {
        var archive = LinuxArchive();
        _release.Serve(Url(Linux("9.9.10").Name, "9.9.10"), archive);
        _release.Serve(Url("checksums.txt", "9.9.10"), $"{Archives.Sha256(archive)}  {Linux("9.9.10").Name}\n");
        var installer = new CliInstaller(new HttpClient(_release), _cache.Path, _log, Version, ChecksumFile.Parse(""));

        var binary = await installer.EnsureInstalledAsync("9.9.10", Linux("9.9.10"), TestContext.Current.CancellationToken);

        binary.ShouldBe(_cache.Combine("9.9.10", "linux_amd64", "inngest"));
        _release.Requests.ShouldContain(Url("checksums.txt", "9.9.10"));
        _log.ToString().ShouldContain("not the 9.9.9");
    }

    [Fact]
    public async Task Another_version_that_differs_from_the_checksums_of_its_release_is_refused()
    {
        _release.Serve(Url(Linux("9.9.10").Name, "9.9.10"), LinuxArchive());
        _release.Serve(Url("checksums.txt", "9.9.10"), $"{Archives.Sha256("something else"u8.ToArray())}  {Linux("9.9.10").Name}\n");
        var installer = new CliInstaller(new HttpClient(_release), _cache.Path, _log, Version, ChecksumFile.Parse(""));

        var exception = await Should.ThrowAsync<ToolException>(
            () => installer.EnsureInstalledAsync("9.9.10", Linux("9.9.10"), TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("does not match");
        Directory.Exists(_cache.Combine("9.9.10", "linux_amd64")).ShouldBeFalse();
    }

    [Fact]
    public async Task A_release_whose_checksums_leave_the_asset_out_is_refused_before_the_download()
    {
        _release.Serve(Url("checksums.txt", "9.9.10"), $"{Archives.Sha256(LinuxArchive())}  inngest_9.9.10_darwin_arm64.tar.gz\n");
        var installer = new CliInstaller(new HttpClient(_release), _cache.Path, _log, Version, ChecksumFile.Parse(""));

        var exception = await Should.ThrowAsync<ToolException>(
            () => installer.EnsureInstalledAsync("9.9.10", Linux("9.9.10"), TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("no entry for inngest_9.9.10_linux_amd64.tar.gz");
        _release.Requests.ShouldNotContain(Url(Linux("9.9.10").Name, "9.9.10"));
    }

    [Fact]
    public async Task A_version_without_a_release_says_so()
    {
        var installer = new CliInstaller(new HttpClient(_release), _cache.Path, _log, Version, ChecksumFile.Parse(""));

        var exception = await Should.ThrowAsync<ToolException>(
            () => installer.EnsureInstalledAsync("9.9.11", Linux("9.9.11"), TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("does not exist (404");
        exception.SuggestsDocker.ShouldBeFalse();
    }

    [Fact]
    public async Task A_download_that_fails_suggests_docker()
    {
        var installer = Installer(LinuxArchive());
        _release.Serve(Url(Linux().Name), [], HttpStatusCode.ServiceUnavailable);

        var exception = await Should.ThrowAsync<ToolException>(
            () => installer.EnsureInstalledAsync(Version, Linux(), TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("503");
        exception.SuggestsDocker.ShouldBeTrue();
    }

    [Fact]
    public async Task A_connection_that_fails_suggests_docker()
    {
        var installer = new CliInstaller(
            new HttpClient(new FailingHandler()), _cache.Path, _log, Version, ChecksumFile.Parse($"{Archives.Sha256([])}  {Linux().Name}"));

        var exception = await Should.ThrowAsync<ToolException>(
            () => installer.EnsureInstalledAsync(Version, Linux(), TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("no route to host");
        exception.SuggestsDocker.ShouldBeTrue();
    }

    [Fact]
    public async Task A_connection_that_ends_before_the_file_does_suggests_docker_and_leaves_nothing_behind()
    {
        var installer = new CliInstaller(
            new HttpClient(new CutHandler()), _cache.Path, _log, Version, ChecksumFile.Parse($"{Archives.Sha256([])}  {Linux().Name}"));

        var exception = await Should.ThrowAsync<ToolException>(
            () => installer.EnsureInstalledAsync(Version, Linux(), TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("connection reset");
        exception.SuggestsDocker.ShouldBeTrue();
        Directory.GetFileSystemEntries(Path.Combine(_cache.Path, Version)).ShouldBeEmpty();
    }

    // The Timeout of an HttpClient ends when the headers arrive, so what is limited here is the time between one read of
    // the body and the next.
    [Fact(Timeout = 30_000)]
    public async Task A_body_that_stops_arriving_is_given_up_on_and_suggests_docker()
    {
        using LoopbackServer server = new(async (stream, token) =>
        {
            await LoopbackServer.WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 1000\r\n\r\n0123456789", token);
            await Task.Delay(Timeout.Infinite, token);
        });
        var installer = new CliInstaller(
            new HttpClient(server.Handler()), _cache.Path, _log, Version, ChecksumFile.Parse($"{Archives.Sha256([])}  {Linux().Name}"), TimeSpan.FromMilliseconds(500));

        var exception = await Should.ThrowAsync<ToolException>(
            () => installer.EnsureInstalledAsync(Version, Linux(), TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("no data arrived within 0.5 seconds");
        exception.SuggestsDocker.ShouldBeTrue();
        _log.ToString().ShouldContain("Downloading inngest_9.9.9_linux_amd64.tar.gz", Case.Sensitive, "the headers arrived and the download began");
        Directory.GetFileSystemEntries(Path.Combine(_cache.Path, Version)).ShouldBeEmpty();
    }

    // The clock is the test's. Each piece arrives 9 seconds after the client began to wait for it, which is a second short
    // of the limit, so that the download takes 45 seconds in all and no read takes longer than the limit.
    [Fact(Timeout = 30_000)]
    public async Task Every_read_of_the_body_gets_the_whole_time_again()
    {
        var archive = LinuxArchive();
        ScriptedBody body = new([.. archive.Chunk((archive.Length / 5) + 1)]);
        FakeTimeProvider clock = new();
        var installer = new CliInstaller(
            new HttpClient(body),
            _cache.Path,
            _log,
            Version,
            ChecksumFile.Parse($"{Archives.Sha256(archive)}  {Linux().Name}"),
            TimeSpan.FromSeconds(10),
            clock);
        var install = installer.EnsureInstalledAsync(Version, Linux(), TestContext.Current.CancellationToken);

        for (var i = 0; i < body.Pieces; i++)
        {
            await body.WaitingForAsync(i);
            clock.Advance(TimeSpan.FromSeconds(9));
            body.Deliver(i);
        }

        var binary = await install;
        File.ReadAllBytes(binary).ShouldBe(Archives.Binary);
    }

    [Fact(Timeout = 30_000)]
    public async Task A_read_that_waits_for_longer_than_the_limit_ends_the_download()
    {
        var archive = LinuxArchive();
        ScriptedBody body = new([.. archive.Chunk((archive.Length / 5) + 1)]);
        FakeTimeProvider clock = new();
        var installer = new CliInstaller(
            new HttpClient(body),
            _cache.Path,
            _log,
            Version,
            ChecksumFile.Parse($"{Archives.Sha256(archive)}  {Linux().Name}"),
            TimeSpan.FromSeconds(10),
            clock);
        var install = installer.EnsureInstalledAsync(Version, Linux(), TestContext.Current.CancellationToken);

        await body.WaitingForAsync(0);
        clock.Advance(TimeSpan.FromSeconds(9));
        body.Deliver(0);
        await body.WaitingForAsync(1);
        clock.Advance(TimeSpan.FromSeconds(11));

        var exception = await Should.ThrowAsync<ToolException>(() => install);
        exception.Message.ShouldContain("no data arrived within 10 seconds");
        exception.SuggestsDocker.ShouldBeTrue();
        Directory.GetFileSystemEntries(Path.Combine(_cache.Path, Version)).ShouldBeEmpty();
    }

    // With ResponseHeadersRead the Timeout of the HttpClient ends when the headers arrive, so the body is the stall limit's
    // alone. The body here comes in three pieces over more than the Timeout.
    [Fact(Timeout = 30_000)]
    public async Task The_timeout_of_the_http_client_does_not_apply_to_the_body()
    {
        var archive = LinuxArchive();
        using LoopbackServer server = new(async (stream, token) =>
        {
            await LoopbackServer.WriteAsync(stream, $"HTTP/1.1 200 OK\r\nContent-Length: {archive.Length}\r\n\r\n", token);
            foreach (var piece in archive.Chunk((archive.Length / 3) + 1))
            {
                await Task.Delay(400, token);
                await stream.WriteAsync(piece, token);
                await stream.FlushAsync(token);
            }
        });
        var installer = new CliInstaller(
            new HttpClient(server.Handler()) { Timeout = TimeSpan.FromSeconds(1) },
            _cache.Path,
            _log,
            Version,
            ChecksumFile.Parse($"{Archives.Sha256(archive)}  {Linux().Name}"),
            TimeSpan.FromSeconds(30));

        var binary = await installer.EnsureInstalledAsync(Version, Linux(), TestContext.Current.CancellationToken);

        File.ReadAllBytes(binary).ShouldBe(Archives.Binary);
    }

    [Fact(Timeout = 30_000)]
    public async Task A_server_that_never_answers_is_given_up_on_and_suggests_docker()
    {
        var installer = new CliInstaller(
            new HttpClient(new HangingHandler()), _cache.Path, _log, Version, ChecksumFile.Parse($"{Archives.Sha256([])}  {Linux().Name}"), TimeSpan.FromMilliseconds(300));

        var exception = await Should.ThrowAsync<ToolException>(
            () => installer.EnsureInstalledAsync(Version, Linux(), TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("no data arrived within 0.3 seconds");
        exception.SuggestsDocker.ShouldBeTrue();
    }

    [Fact(Timeout = 30_000)]
    public async Task A_timeout_of_the_http_client_is_not_taken_for_the_user_pressing_ctrl_c()
    {
        var installer = new CliInstaller(
            new HttpClient(new HangingHandler()) { Timeout = TimeSpan.FromMilliseconds(200) },
            _cache.Path,
            _log,
            Version,
            ChecksumFile.Parse($"{Archives.Sha256([])}  {Linux().Name}"));

        var exception = await Should.ThrowAsync<ToolException>(
            () => installer.EnsureInstalledAsync(Version, Linux(), TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("no data arrived");
        exception.SuggestsDocker.ShouldBeTrue();
    }

    [Fact(Timeout = 30_000)]
    public async Task Checksums_that_stop_arriving_are_given_up_on_too()
    {
        using LoopbackServer server = new(async (stream, token) =>
        {
            await LoopbackServer.WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 600\r\n\r\nabc", token);
            await Task.Delay(Timeout.Infinite, token);
        });
        var installer = new CliInstaller(
            new HttpClient(server.Handler()), _cache.Path, _log, Version, ChecksumFile.Parse(""), TimeSpan.FromMilliseconds(500));

        var exception = await Should.ThrowAsync<ToolException>(
            () => installer.EnsureInstalledAsync("9.9.10", Linux("9.9.10"), TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("no data arrived within 0.5 seconds");
        exception.SuggestsDocker.ShouldBeTrue();
    }

    [Fact(Timeout = 30_000)]
    public async Task The_user_cancelling_is_still_a_cancellation()
    {
        var installer = new CliInstaller(
            new HttpClient(new HangingHandler()), _cache.Path, _log, Version, ChecksumFile.Parse($"{Archives.Sha256([])}  {Linux().Name}"), TimeSpan.FromMinutes(5));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Should.ThrowAsync<OperationCanceledException>(() => installer.EnsureInstalledAsync(Version, Linux(), cancellation.Token));
    }

    [Theory]
    [InlineData("../9.9.9")]
    [InlineData("9.9.9\n")]
    [InlineData("\u0669.\u0669.\u0669")]
    [InlineData("v9.9.9")]
    [InlineData("")]
    public async Task A_version_that_could_name_another_folder_is_never_used_whoever_asks(string version)
    {
        var installer = Installer(LinuxArchive());

        await Should.ThrowAsync<ArgumentException>(() => installer.EnsureInstalledAsync(version, Linux(), TestContext.Current.CancellationToken));

        _release.Requests.ShouldBeEmpty();
        Directory.GetFileSystemEntries(_cache.Path).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_archive_whose_binary_has_no_content_is_refused()
    {
        var installer = Installer(Archives.TarGz(("inngest", [])));

        var exception = await Should.ThrowAsync<ToolException>(
            () => installer.EnsureInstalledAsync(Version, Linux(), TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("no content");
        Directory.Exists(CliFolder()).ShouldBeFalse();
    }

    [Fact]
    public async Task An_archive_without_the_binary_is_refused()
    {
        var installer = Installer(Archives.TarGz(("LICENSE.md", "license"u8.ToArray())));

        var exception = await Should.ThrowAsync<ToolException>(
            () => installer.EnsureInstalledAsync(Version, Linux(), TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("does not contain inngest");
        Directory.Exists(CliFolder()).ShouldBeFalse();
    }

    [Fact]
    public async Task A_file_that_is_not_an_archive_is_refused_although_its_checksum_matches()
    {
        var installer = Installer("not a gzip file"u8.ToArray());

        var exception = await Should.ThrowAsync<ToolException>(
            () => installer.EnsureInstalledAsync(Version, Linux(), TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("not a valid archive");
    }

    // The names in an archive never decide where a file goes: only the entry for the binary is read, and it is written
    // where the tool says.
    [Fact]
    public async Task Entries_that_climb_out_of_the_folder_are_not_unpacked()
    {
        var installer = Installer(Archives.TarGz(("../../escaped", "x"u8.ToArray()), ("sub/inngest", "x"u8.ToArray()), ("./inngest", Archives.Binary)));

        var binary = await installer.EnsureInstalledAsync(Version, Linux(), TestContext.Current.CancellationToken);

        File.ReadAllBytes(binary).ShouldBe(Archives.Binary, "the entry ./inngest is the binary");
        File.Exists(Path.Combine(_cache.Path, "escaped")).ShouldBeFalse();
        File.Exists(Path.Combine(_cache.Path, "..", "escaped")).ShouldBeFalse();
        Directory.GetFileSystemEntries(CliFolder()).Length.ShouldBe(1);
    }

    [Theory]
    [InlineData(TarEntryFormat.Ustar)]
    [InlineData(TarEntryFormat.Pax)]
    [InlineData(TarEntryFormat.Gnu)]
    public async Task The_binary_is_found_in_a_tar_archive_of_any_format(TarEntryFormat format)
    {
        var installer = Installer(Archives.TarGz(format, ("LICENSE.md", new byte[30_630]), ("inngest", Archives.Binary)));

        var binary = await installer.EnsureInstalledAsync(Version, Linux(), TestContext.Current.CancellationToken);

        File.ReadAllBytes(binary).ShouldBe(Archives.Binary);
    }

    // On a stream that cannot seek, the reader of .NET 8 skips one byte too few over the unread data of an entry that is a
    // single byte long, and then cannot parse the next header. The installer reads the data of the entries it skips.
    [Theory]
    [InlineData(TarEntryFormat.Ustar)]
    [InlineData(TarEntryFormat.Pax)]
    [InlineData(TarEntryFormat.Gnu)]
    public async Task Entries_of_one_byte_in_front_of_the_binary_are_skipped_on_every_runtime(TarEntryFormat format)
    {
        var installer = Installer(Archives.TarGz(format, ("VERSION", [1]), ("LICENSE.md", new byte[30_630]), ("NOTICE", [1]), ("inngest", Archives.Binary)));

        var binary = await installer.EnsureInstalledAsync(Version, Linux(), TestContext.Current.CancellationToken);

        File.ReadAllBytes(binary).ShouldBe(Archives.Binary);
    }

    [Fact]
    public async Task A_folder_without_the_binary_is_not_an_installation()
    {
        Directory.CreateDirectory(CliFolder());
        File.WriteAllText(Path.Combine(CliFolder(), "inngest.partial"), "half");
        var installer = Installer(LinuxArchive());

        var binary = await installer.EnsureInstalledAsync(Version, Linux(), TestContext.Current.CancellationToken);

        File.Exists(binary).ShouldBeTrue();
    }

    [Fact]
    public async Task Staging_folders_of_an_install_that_was_killed_are_cleaned_up_once_a_day_old()
    {
        var old = Directory.CreateDirectory(_cache.Combine(Version, ".staging-old"));
        var recent = Directory.CreateDirectory(_cache.Combine(Version, ".staging-recent"));
        old.LastWriteTimeUtc = DateTime.UtcNow.AddDays(-2);
        var installer = Installer(LinuxArchive());

        await installer.EnsureInstalledAsync(Version, Linux(), TestContext.Current.CancellationToken);

        Directory.Exists(old.FullName).ShouldBeFalse();
        Directory.Exists(recent.FullName).ShouldBeTrue("it may belong to an install that is running");
    }

    [Fact]
    public async Task A_cancelled_download_leaves_nothing_behind()
    {
        var installer = new CliInstaller(
            new HttpClient(new HangingHandler()), _cache.Path, _log, Version, ChecksumFile.Parse($"{Archives.Sha256([])}  {Linux().Name}"));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Should.ThrowAsync<OperationCanceledException>(() => installer.EnsureInstalledAsync(Version, Linux(), cancellation.Token));

        Directory.GetFileSystemEntries(Path.Combine(_cache.Path, Version)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Two_installs_at_once_end_with_one_good_installation()
    {
        var archive = LinuxArchive();
        var first = Installer(archive);
        var second = Installer(archive);

        var binaries = await Task.WhenAll(
            first.EnsureInstalledAsync(Version, Linux(), TestContext.Current.CancellationToken),
            second.EnsureInstalledAsync(Version, Linux(), TestContext.Current.CancellationToken));

        binaries[0].ShouldBe(binaries[1]);
        File.ReadAllBytes(binaries[0]).ShouldBe(Archives.Binary);
        Directory.GetFileSystemEntries(Path.Combine(_cache.Path, Version)).Select(Path.GetFileName).ShouldBe(["linux_amd64"]);
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("no route to host");
    }

    // A response that promises a hundred bytes, sends ten and then loses the connection.
    private sealed class CutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new CutContent() });
    }

    private sealed class CutContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 100;
            return true;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new CutStream());
    }

    private sealed class CutStream : Stream
    {
        private bool _sent;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_sent)
            {
                throw new IOException("connection reset");
            }

            _sent = true;
            buffer.Span[..10].Fill(1);
            return ValueTask.FromResult(10);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage();
        }
    }
}
