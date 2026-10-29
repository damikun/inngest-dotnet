using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Inngest.Soak.Docker;

internal static partial class DockerCli
{
    public static async Task<(int ExitCode, string Output, string Error)> RunAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start docker.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await output, await error);
    }

    // The first line of the answer to a raw HTTP request sent from inside a container, through bash's /dev/tcp. The
    // Inngest image has bash but no curl.
    public static async Task<string> HttpStatusLineFromAsync(string container, string host, int port, string request, CancellationToken cancellationToken)
    {
        var script = $"exec 3<>/dev/tcp/{host}/{port} && printf '%s' \"$1\" >&3 && head -n 1 <&3";
        var (exitCode, output, error) = await RunAsync(["exec", container, "bash", "-c", script, "probe", request], cancellationToken);
        return exitCode == 0 ? output.Trim() : $"failed ({exitCode}): {error.Trim()}";
    }

    public static async Task<string[]> LogsAsync(string container, CancellationToken cancellationToken)
    {
        // Containers log to stdout and stderr; docker logs replays them on the same two streams.
        var (_, output, error) = await RunAsync(["logs", container], cancellationToken);
        return [.. (output + error).Split('\n', StringSplitOptions.RemoveEmptyEntries)];
    }

    public static async Task<JsonElement> InspectAsync(string container, CancellationToken cancellationToken)
    {
        var (exitCode, output, error) = await RunAsync(["inspect", container], cancellationToken);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"docker inspect {container} failed: {error}");
        }

        using var document = JsonDocument.Parse(output);
        return document.RootElement[0].Clone();
    }

    // "52.37MiB", "1.2GB", "512B": docker prints binary units for memory and decimal ones for I/O.
    public static long ParseSize(string text)
    {
        var match = SizePattern().Match(text.Trim());
        if (!match.Success)
        {
            return 0;
        }

        var value = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var factor = match.Groups[2].Value switch
        {
            "KiB" => 1024d,
            "MiB" => 1024d * 1024,
            "GiB" => 1024d * 1024 * 1024,
            "kB" => 1000d,
            "MB" => 1000d * 1000,
            "GB" => 1000d * 1000 * 1000,
            _ => 1d,
        };
        return (long)(value * factor);
    }

    [GeneratedRegex(@"^([0-9.]+)\s*([A-Za-z]*)$")]
    private static partial Regex SizePattern();
}

// Streams `docker stats` for some containers and keeps one row per container per refresh.
internal sealed class DockerStats : IAsyncDisposable
{
    public const string Header = "elapsed_s,container,cpu_percent,memory_bytes,memory_limit_bytes,memory_percent,net_in_bytes,net_out_bytes,block_in_bytes,block_out_bytes,pids";

    private readonly Process _process;
    private readonly Stopwatch _clock;
    private readonly Task _reading;
    private readonly List<StatsRow> _rows = [];

    private DockerStats(Process process, Stopwatch clock)
    {
        _process = process;
        _clock = clock;
        _reading = ReadAsync();
    }

    public IReadOnlyList<StatsRow> Rows
    {
        get
        {
            lock (_rows)
            {
                return [.. _rows];
            }
        }
    }

    public static DockerStats Start(IEnumerable<string> containers, Stopwatch clock)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("stats");
        start.ArgumentList.Add("--format");
        start.ArgumentList.Add("{{json .}}");
        foreach (var container in containers)
        {
            start.ArgumentList.Add(container);
        }

        return new DockerStats(Process.Start(start) ?? throw new InvalidOperationException("Could not start docker stats."), clock);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill();
        }

        await _reading.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _process.Dispose();
    }

    private async Task ReadAsync()
    {
        while (await _process.StandardOutput.ReadLineAsync() is { } line)
        {
            // Without a terminal docker still moves the cursor between refreshes; the JSON sits between the codes.
            var start = line.IndexOf('{');
            var end = line.LastIndexOf('}');
            if (start < 0 || end < start)
            {
                continue;
            }

            using var json = JsonDocument.Parse(line.AsMemory(start, end - start + 1));
            var root = json.RootElement;
            var memory = root.GetProperty("MemUsage").GetString()!.Split('/');
            var net = root.GetProperty("NetIO").GetString()!.Split('/');
            var block = root.GetProperty("BlockIO").GetString()!.Split('/');
            var row = new StatsRow(
                _clock.Elapsed.TotalSeconds,
                root.GetProperty("Name").GetString()!,
                double.Parse(root.GetProperty("CPUPerc").GetString()!.TrimEnd('%'), CultureInfo.InvariantCulture),
                DockerCli.ParseSize(memory[0]),
                DockerCli.ParseSize(memory[1]),
                double.Parse(root.GetProperty("MemPerc").GetString()!.TrimEnd('%'), CultureInfo.InvariantCulture),
                DockerCli.ParseSize(net[0]),
                DockerCli.ParseSize(net[1]),
                DockerCli.ParseSize(block[0]),
                DockerCli.ParseSize(block[1]),
                int.TryParse(root.GetProperty("PIDs").GetString(), CultureInfo.InvariantCulture, out var pids) ? pids : 0);
            lock (_rows)
            {
                _rows.Add(row);
            }
        }
    }
}

internal sealed record StatsRow(
    double ElapsedSeconds,
    string Container,
    double CpuPercent,
    long MemoryBytes,
    long MemoryLimitBytes,
    double MemoryPercent,
    long NetInBytes,
    long NetOutBytes,
    long BlockInBytes,
    long BlockOutBytes,
    int Pids)
{
    public string ToCsv() => string.Join(',',
        ElapsedSeconds.ToString("0.0", CultureInfo.InvariantCulture),
        Container,
        CpuPercent.ToString("0.00", CultureInfo.InvariantCulture),
        MemoryBytes,
        MemoryLimitBytes,
        MemoryPercent.ToString("0.00", CultureInfo.InvariantCulture),
        NetInBytes,
        NetOutBytes,
        BlockInBytes,
        BlockOutBytes,
        Pids);
}
