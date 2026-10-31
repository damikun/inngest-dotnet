using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
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

// A server on the loopback interface that answers each request with whatever the test writes to the connection, so
// that the real HTTP client, and not a fake one, sees a body that arrives slowly or stops.
internal sealed class LoopbackServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<Stream, CancellationToken, Task> _respond;

    public LoopbackServer(Func<Stream, CancellationToken, Task> respond)
    {
        _respond = respond;
        _listener.Start();
        _ = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    // Sends the requests an HttpClient makes for the release on GitHub to this server instead.
    public HttpMessageHandler Handler() => new RedirectHandler(Port);

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }

    public static async Task WriteAsync(Stream stream, string text, CancellationToken cancellationToken) =>
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text), cancellationToken);

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = Task.Run(() => ServeAsync(client));
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var buffer = new byte[8192];
                var length = 0;
                while (!Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(length), _stop.Token);
                    if (read == 0)
                    {
                        return;
                    }

                    length += read;
                }

                await _respond(stream, _stop.Token);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }
    }

    private sealed class RedirectHandler(int port) : DelegatingHandler(new SocketsHttpHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri = new Uri($"http://127.0.0.1:{port}{request.RequestUri!.PathAndQuery}");
            return base.SendAsync(request, cancellationToken);
        }
    }
}

// A response whose body arrives in the pieces and at the moments the test chooses. Before it waits for a piece the
// stream says so, which is the moment the client has started the clock for that read.
internal sealed class ScriptedBody : HttpMessageHandler
{
    private readonly byte[][] _pieces;
    private readonly TaskCompletionSource[] _waiting;
    private readonly TaskCompletionSource[] _released;

    public ScriptedBody(byte[][] pieces)
    {
        _pieces = pieces;
        _waiting = [.. pieces.Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))];
        _released = [.. pieces.Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))];
    }

    public int Pieces => _pieces.Length;

    // Completes when the client waits for piece i.
    public Task WaitingForAsync(int i) => _waiting[i].Task;

    public void Deliver(int i) => _released[i].SetResult();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ScriptedContent(this) });

    private sealed class ScriptedContent(ScriptedBody body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = body._pieces.Sum(piece => piece.Length);
            return true;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new ScriptedStream(body));
    }

    private sealed class ScriptedStream(ScriptedBody body) : Stream
    {
        private int _next;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_next >= body.Pieces)
            {
                return 0;
            }

            var index = _next++;
            body._waiting[index].TrySetResult();
            await body._released[index].Task.WaitAsync(cancellationToken);
            body._pieces[index].CopyTo(buffer);
            return body._pieces[index].Length;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
