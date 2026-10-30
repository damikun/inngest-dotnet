using System.Net;
using System.Runtime.InteropServices;
using Inngest.Tool;
using Inngest.Tool.Download;

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

    private static byte[] LinuxArchive() => Archives.TarGz(("inngest", Archives.Binary), ("LICENSE.md", "license"u8.ToArray()));

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
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(200));

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
