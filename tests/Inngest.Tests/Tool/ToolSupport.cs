using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Inngest.Tests.Tool;

// A release of the Inngest CLI that a test serves from memory: URLs map to bodies, and every request is recorded.
internal sealed class FakeReleaseHandler : HttpMessageHandler
{
    private readonly Dictionary<string, (HttpStatusCode Status, byte[] Body)> _routes = [];
    private readonly Lock _gate = new();
    private readonly List<string> _requests = [];

    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public FakeReleaseHandler Serve(string url, byte[] body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes[url] = (status, body);
        return this;
    }

    public FakeReleaseHandler Serve(string url, string body) => Serve(url, Encoding.UTF8.GetBytes(body));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        lock (_gate)
        {
            _requests.Add(url);
        }

        if (!_routes.TryGetValue(url, out var route))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        return Task.FromResult(new HttpResponseMessage(route.Status) { Content = new ByteArrayContent(route.Body) });
    }
}

// A folder that exists for the length of a test.
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "inngest-tool-tests-" + Guid.NewGuid().ToString("N"));

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal static class Archives
{
    public static readonly byte[] Binary = Encoding.UTF8.GetBytes("#!/bin/sh\necho inngest\n");

    // The archive GoReleaser makes for Linux and macOS: the binary and the license at the top, the binary without the
    // execute bit as far as a test can tell.
    public static byte[] TarGz(params (string Name, byte[] Content)[] entries)
    {
        using MemoryStream buffer = new();
        using (GZipStream gzip = new(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (TarWriter writer = new(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(content),
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                });
            }
        }

        return buffer.ToArray();
    }

    public static byte[] Zip(params (string Name, byte[] Content)[] entries)
    {
        using MemoryStream buffer = new();
        using (ZipArchive zip = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(content);
            }
        }

        return buffer.ToArray();
    }

    public static string Sha256(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}
