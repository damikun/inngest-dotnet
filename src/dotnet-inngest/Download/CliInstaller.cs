// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;

namespace Inngest.Tool.Download;

// Puts a release of the Inngest CLI on disk, once per user and version, and returns the path of its binary:
// <cache>/<version>/<os>_<arch>/inngest. The pinned version is checked against the checksums built into the tool, any
// other against the checksums.txt of its release.
internal sealed class CliInstaller(
    HttpClient http,
    string cacheRoot,
    TextWriter log,
    string pinnedVersion = CliRelease.PinnedVersion,
    ChecksumFile? pinnedChecksums = null,
    TimeSpan? stallTimeout = null,
    TimeProvider? timeProvider = null)
{
    private const string StagingPrefix = ".staging-";
    private const int BufferSize = 81920;
    private const int MoveAttempts = 4;

    private readonly ChecksumFile _pinned = pinnedChecksums ?? PinnedChecksums.Checksums;

    // How long the server may send nothing: before the headers, and between one read of the body and the next. A limit
    // on the whole download would cut off a slow connection that is alive, and the Timeout of an HttpClient ends when
    // the headers arrive, so a body that stalls would otherwise be waited for forever.
    private readonly TimeSpan _stallTimeout = stallTimeout ?? TimeSpan.FromSeconds(60);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<string> EnsureInstalledAsync(string version, CliAsset asset, CancellationToken cancellationToken)
    {
        // The version names a folder and is part of a URL, so the installer does not take it on trust from its caller.
        if (!CliRelease.TryNormalizeVersion(version, out var normalized) || normalized != version)
        {
            throw new ArgumentException($"'{version}' is not a release version of the Inngest CLI.", nameof(version));
        }

        var directory = Path.Combine(cacheRoot, version, asset.Platform);
        var binary = Path.Combine(directory, asset.BinaryName);
        if (File.Exists(binary))
        {
            return binary;
        }

        var expected = await ExpectedChecksumAsync(version, asset, cancellationToken);

        try
        {
            await InstallAsync(version, asset, expected, directory, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ToolException(
                $"Could not install the Inngest CLI in {directory}: {exception.Message} INNGEST_DOTNET_CACHE names another folder for it.",
                exception);
        }

        log.WriteLine($"Installed the Inngest CLI {version} in {directory}.");
        return binary;
    }

    private async Task InstallAsync(string version, CliAsset asset, string expected, string directory, CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(directory)!;
        Directory.CreateDirectory(parent);
        RemoveStaleStaging(parent);

        // The download and the unpacked binary are made in a folder of their own beside the final one. An interrupted
        // install leaves nothing that looks installed, and the folder is moved into place in one step.
        var staging = Path.Combine(parent, StagingPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var archive = Path.Combine(staging, asset.Name);
            var actual = await DownloadAsync(CliRelease.AssetUri(version, asset.Name), archive, cancellationToken);
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                throw new ToolException(
                    $"{asset.Name} does not match its SHA-256, so it was not installed.{Environment.NewLine}"
                    + $"  expected {expected}{Environment.NewLine}  received {actual}");
            }

            var unpacked = Path.Combine(staging, "cli");
            Directory.CreateDirectory(unpacked);
            Extract(archive, asset, Path.Combine(unpacked, asset.BinaryName));

            // A folder without the binary is what a hand-made folder or a cleanup that stopped halfway leaves. Moving
            // is atomic, so a folder another install put there has its binary.
            if (Directory.Exists(directory) && !File.Exists(Path.Combine(directory, asset.BinaryName)))
            {
                Directory.Delete(directory, recursive: true);
            }

            await MoveIntoPlaceAsync(unpacked, directory, Path.Combine(directory, asset.BinaryName), cancellationToken);
        }
        finally
        {
            Delete(staging);
        }
    }

    private static async Task MoveIntoPlaceAsync(string unpacked, string directory, string binary, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(unpacked, directory);
                return;
            }
            catch (IOException) when (File.Exists(binary))
            {
                // A second tool installed the same release first. Its copy is as good as this one.
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && attempt < MoveAttempts)
            {
                // A virus scanner on Windows can hold a file it has just seen written for a moment.
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken);
            }
        }
    }

    private async Task<string> ExpectedChecksumAsync(string version, CliAsset asset, CancellationToken cancellationToken)
    {
        ChecksumFile checksums;
        if (version == pinnedVersion)
        {
            checksums = _pinned;
        }
        else
        {
            log.WriteLine(
                $"Version {version} is not the {pinnedVersion} that this tool carries the checksums of, so its download is checked "
                + $"against the {CliRelease.ChecksumsFileName} of the release. That catches a damaged download, not a replaced release.");
            checksums = ChecksumFile.Parse(await GetTextAsync(CliRelease.ChecksumsUri(version), cancellationToken));
        }

        return checksums.TryGet(asset.Name, out var expected)
            ? expected
            : throw new ToolException($"The checksums have no entry for {asset.Name}.");
    }

    private async Task<string> DownloadAsync(Uri uri, string path, CancellationToken cancellationToken)
    {
        using var response = await GetAsync(uri, cancellationToken);
        var size = response.Content.Headers.ContentLength;
        log.WriteLine(size is { } bytes
            ? string.Create(CultureInfo.InvariantCulture, $"Downloading {uri.Segments[^1]} ({bytes / 1048576.0:F1} MB)...")
            : $"Downloading {uri.Segments[^1]}...");

        await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var watch = Watch(idle);
        var buffer = new byte[BufferSize];
        while (true)
        {
            // Every read gets the whole time again.
            watch.Change(_stallTimeout, Timeout.InfiniteTimeSpan);
            int read;
            try
            {
                read = await source.ReadAsync(buffer, idle.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw Stalled(uri);
            }
            catch (IOException exception)
            {
                // The connection ended before the file did. A file can also be damaged without that, which the checksum
                // catches.
                throw new ToolException($"Downloading {uri} failed: {exception.Message}", exception) { SuggestsDocker = true };
            }

            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    // For the checksums, a few hundred bytes, one limit on the request as a whole is a limit on how long it can stall.
    private async Task<string> GetTextAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var watch = Watch(deadline);
        try
        {
            using var response = await GetAsync(uri, deadline.Token);
            return await response.Content.ReadAsStringAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Stalled(uri);
        }
        catch (IOException exception)
        {
            throw new ToolException($"Downloading {uri} failed: {exception.Message}", exception) { SuggestsDocker = true };
        }
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var headers = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var watch = Watch(headers);
        try
        {
            var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, headers.Token);
            if (!response.IsSuccessStatusCode)
            {
                var status = $"{(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd();
                var missing = response.StatusCode == HttpStatusCode.NotFound;
                response.Dispose();
                throw new ToolException(missing
                    ? $"{uri} does not exist ({status}). Check the version against https://github.com/inngest/inngest/releases."
                    : $"Downloading {uri} failed: {status}.")
                {
                    SuggestsDocker = !missing,
                };
            }

            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our limit, or the Timeout of the HttpClient. Neither is the user pressing Ctrl+C.
            throw Stalled(uri);
        }
        catch (HttpRequestException exception)
        {
            throw new ToolException($"Downloading {uri} failed: {exception.Message}", exception) { SuggestsDocker = true };
        }
    }

    // Cancels the source when the timer is not restarted within the stall timeout. A timer of the time provider, so that
    // the tests can move the clock instead of waiting.
    private ITimer Watch(CancellationTokenSource source) =>
        _time.CreateTimer(static state => ((CancellationTokenSource)state!).Cancel(), source, _stallTimeout, Timeout.InfiniteTimeSpan);

    private ToolException Stalled(Uri uri) => new(
        $"Downloading {uri} stopped: no data arrived within {_stallTimeout.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture)} seconds.")
    {
        SuggestsDocker = true,
    };

    // Only the binary is unpacked. The archive's other file is the license, and a name in an archive never decides where
    // anything is written.
    private static void Extract(string archive, CliAsset asset, string destination)
    {
        try
        {
            var found = asset.Format == ArchiveFormat.Zip
                ? ExtractZip(archive, asset.BinaryName, destination)
                : ExtractTar(archive, asset.BinaryName, destination);
            if (!found)
            {
                throw new ToolException($"{asset.Name} does not contain {asset.BinaryName}.");
            }
        }
        catch (InvalidDataException exception)
        {
            throw new ToolException($"{asset.Name} is not a valid archive: {exception.Message}", exception);
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                destination,
                File.GetUnixFileMode(destination) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
    }

    private static bool ExtractTar(string archive, string binaryName, string destination)
    {
        using var file = File.OpenRead(archive);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile && IsAtTop(entry.Name, binaryName))
            {
                Write(entry.DataStream, destination);
                return true;
            }
        }

        return false;
    }

    private static bool ExtractZip(string archive, string binaryName, string destination)
    {
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries)
        {
            if (IsAtTop(entry.FullName, binaryName))
            {
                using var stream = entry.Open();
                Write(stream, destination);
                return true;
            }
        }

        return false;
    }

    // The binary is on disk before the folder that holds it is renamed into place. Otherwise a crash could leave the
    // rename without the content, and a file with no bytes in it is what the next run would find and trust.
    private static void Write(Stream? source, string destination)
    {
        using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        source?.CopyTo(target);
        target.Flush(flushToDisk: true);
        if (target.Length == 0)
        {
            throw new InvalidDataException("the binary in it has no content.");
        }
    }

    // Archives write the name with a leading ./ at times, and zip tools with backslashes.
    private static bool IsAtTop(string entryName, string binaryName)
    {
        var name = entryName.Replace('\\', '/');
        return (name.StartsWith("./", StringComparison.Ordinal) ? name[2..] : name) == binaryName;
    }

    // An install that was killed leaves its staging folder behind. Folders from a day ago cannot belong to one running.
    private static void RemoveStaleStaging(string parent)
    {
        foreach (var directory in Directory.EnumerateDirectories(parent, StagingPrefix + "*"))
        {
            if (Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddDays(-1))
            {
                Delete(directory);
            }
        }
    }

    private static void Delete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left behind, and removed by a later install once it is a day old.
        }
    }
}
