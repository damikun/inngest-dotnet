using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Inngest.Soak.Docker;

// Drives the containerized sample app with summarize-accounts runs. Each run invokes score-account three times in
// parallel, waits for all three and sends an event from a step: four steps, and three more runs with a step each.
// The tool keeps a fixed number of runs in flight, sending a new event whenever runs finish, until all have been
// sent. Afterwards it checks every run's status and output, and writes latencies, docker stats and log problems.
internal static class LoadTest
{
    private const string AppId = "inngest-dotnet-sample";
    private const string Parent = AppId + "-summarize-accounts";
    private const string Child = AppId + "-score-account";
    private const string ParentOutput = """{"accounts":3,"total":170}""";

    private static readonly Dictionary<string, string> ChildOutputs = new(StringComparer.Ordinal)
    {
        ["""{"accountId":"acme","score":40}"""] = "acme",
        ["""{"accountId":"globex","score":60}"""] = "globex",
        ["""{"accountId":"initech","score":70}"""] = "initech",
    };

    public static async Task<int> RunAsync(CommandLine options, CancellationToken cancellationToken)
    {
        var mode = options.Require("mode");
        var runs = options.GetInt("runs", 2000);
        var concurrency = options.GetInt("concurrency", 300);
        var appContainer = options.Require("app-container");
        var serverContainer = options.Require("server-container");
        var output = Path.Combine(options.Get("out", "docs/results/load"), mode);
        var timeout = TimeSpan.FromMinutes(options.GetInt("timeout-minutes", 30));
        Directory.CreateDirectory(output);
        using var server = new InngestServer(new Uri(options.Get("inngest", "http://127.0.0.1:18288")), options.Get("event-key", "dev-mode-accepts-any-key"));

        var functions = await server.FunctionIdsAsync(cancellationToken);
        var parentId = functions[Parent];
        var childId = functions[Child];
        var appInspect = await DockerCli.InspectAsync(appContainer, cancellationToken);

        var clock = Stopwatch.StartNew();
        var sentAt = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var progress = new List<string> { "elapsed_s,sent,finished,in_flight" };
        var sent = 0;
        var finished = 0;
        var timedOut = false;
        double loadStart;
        double loadEnd;
        IReadOnlyList<StatsRow> stats;
        await using (var sampler = DockerStats.Start([appContainer, serverContainer], clock))
        {
            // A few samples of the idle app first, and a few after the load to show where memory settles.
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            var from = DateTimeOffset.UtcNow.AddSeconds(-1);
            loadStart = clock.Elapsed.TotalSeconds;
            Console.WriteLine($"load ({mode}): {runs} runs of {Parent}, {concurrency} in flight");
            var lastReport = 0d;
            while (true)
            {
                finished = await server.CountFinishedAsync(from, [parentId], cancellationToken);
                var room = Math.Min(concurrency - (sent - finished), runs - sent);
                if (room > 0)
                {
                    var batch = Math.Min(room, 100);
                    var events = Enumerable.Range(sent, batch).Select(index => (object)new { name = "sample/summary.requested", data = new { load = mode, index } }).ToList();
                    var ids = await server.SendAsync(events, cancellationToken);
                    var now = DateTimeOffset.UtcNow;
                    foreach (var id in ids)
                    {
                        sentAt[id] = now;
                    }

                    sent += batch;
                }

                var elapsed = clock.Elapsed.TotalSeconds;
                progress.Add(string.Create(CultureInfo.InvariantCulture, $"{elapsed:0.00},{sent},{finished},{sent - finished}"));
                if (elapsed - lastReport >= 10)
                {
                    lastReport = elapsed;
                    Console.WriteLine($"  {elapsed - loadStart,6:0} s  sent {sent,5}  finished {finished,5}  in flight {sent - finished,4}");
                }

                if (finished >= runs)
                {
                    break;
                }

                if (clock.Elapsed.TotalSeconds - loadStart > timeout.TotalSeconds)
                {
                    timedOut = true;
                    break;
                }

                await Task.Delay(250, cancellationToken);
            }

            loadEnd = clock.Elapsed.TotalSeconds;
            Console.WriteLine($"  all {finished} runs finished after {loadEnd - loadStart:0.0} s; checking them");
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            stats = sampler.Rows;

            var results = await CheckRunsAsync(server, from, parentId, childId, sentAt, cancellationToken);
            var problems = await LogProblemsAsync(appContainer, serverContainer, output, cancellationToken);
            var summary = Summarize(mode, runs, concurrency, sent, timedOut, loadStart, loadEnd, results, stats, appContainer, serverContainer, appInspect, problems);

            await File.WriteAllLinesAsync(Path.Combine(output, "progress.csv"), progress, cancellationToken);
            await File.WriteAllLinesAsync(Path.Combine(output, "docker-stats.csv"), [DockerStats.Header, .. stats.Select(static row => row.ToCsv())], cancellationToken);
            await File.WriteAllLinesAsync(Path.Combine(output, "runs.csv"), ["run_id,function,status,queued_at,ended_at,queued_to_ended_ms,sent_to_ended_ms,output_ok", .. results.Rows], cancellationToken);
            var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(output, "summary.json"), json, cancellationToken);
            Console.WriteLine(json);
            return results.AllCorrect && !timedOut && results.Parents == runs ? 0 : 1;
        }
    }

    private static async Task<RunResults> CheckRunsAsync(
        InngestServer server,
        DateTimeOffset from,
        string parentId,
        string childId,
        Dictionary<string, DateTimeOffset> sentAt,
        CancellationToken cancellationToken)
    {
        var listed = new List<RunInfo>();
        await foreach (var run in server.ListRunsAsync(from, [parentId, childId], cancellationToken))
        {
            listed.Add(run);
        }

        // Outputs are not part of the list, so every run is read on its own.
        var outputs = new ConcurrentDictionary<string, RunInfo>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(listed, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = cancellationToken }, async (run, token) =>
        {
            outputs[run.Id] = await server.FunctionRunAsync(run.Id, token);
        });

        var results = new RunResults();
        foreach (var run in listed)
        {
            var detail = outputs[run.Id];
            var isParent = run.FunctionSlug == Parent;
            var canonical = FlowCheck.Canonical(detail.Output);
            var correct = detail.Status == "COMPLETED" && (isParent ? canonical == ParentOutput : ChildOutputs.ContainsKey(canonical));
            var queuedToEnded = detail.EndedAt - run.QueuedAt;
            var sentToEnded = run.TriggerId is { } trigger && sentAt.TryGetValue(trigger, out var sent) ? detail.EndedAt - sent : null;
            results.Add(isParent, correct, detail.Status, canonical, queuedToEnded, sentToEnded);
            results.Rows.Add(string.Join(',',
                run.Id,
                run.FunctionSlug,
                detail.Status,
                run.QueuedAt?.ToString("O", CultureInfo.InvariantCulture),
                detail.EndedAt?.ToString("O", CultureInfo.InvariantCulture),
                queuedToEnded?.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture),
                sentToEnded?.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture),
                correct ? "true" : "false"));
        }

        return results;
    }

    // Warnings and errors from the app's console log and the server's JSON log: counted, and the first 200 of each kept.
    private static async Task<Dictionary<string, object>> LogProblemsAsync(string appContainer, string serverContainer, string output, CancellationToken cancellationToken)
    {
        var app = await DockerCli.LogsAsync(appContainer, cancellationToken);
        var appProblems = app.Where(static line => line.Contains(" warn: ", StringComparison.Ordinal) || line.Contains(" fail: ", StringComparison.Ordinal) || line.Contains(" crit: ", StringComparison.Ordinal)).ToList();
        var server = await DockerCli.LogsAsync(serverContainer, cancellationToken);
        var serverProblems = server.Where(static line => line.Contains("\"level\":\"ERROR\"", StringComparison.Ordinal) || line.Contains("\"level\":\"WARN\"", StringComparison.Ordinal)).ToList();
        await File.WriteAllLinesAsync(Path.Combine(output, "app-problems.log"), appProblems.Take(200), cancellationToken);
        await File.WriteAllLinesAsync(Path.Combine(output, "server-problems.log"), serverProblems.Take(200), cancellationToken);
        return new Dictionary<string, object>
        {
            ["app_log_lines"] = app.Length,
            ["app_warnings"] = appProblems.Count(static line => line.Contains(" warn: ", StringComparison.Ordinal)),
            ["app_errors"] = appProblems.Count(static line => !line.Contains(" warn: ", StringComparison.Ordinal)),
            ["server_log_lines"] = server.Length,
            ["server_warnings"] = serverProblems.Count(static line => line.Contains("\"level\":\"WARN\"", StringComparison.Ordinal)),
            ["server_errors"] = serverProblems.Count(static line => line.Contains("\"level\":\"ERROR\"", StringComparison.Ordinal)),
            ["server_problem_messages"] = serverProblems
                .Select(static line => JsonDocument.Parse(line).RootElement.TryGetProperty("msg", out var message) ? message.GetString() ?? "" : "")
                .GroupBy(static message => message)
                .ToDictionary(static group => group.Key, static group => group.Count()),
        };
    }

    private static Dictionary<string, object?> Summarize(
        string mode,
        int runs,
        int concurrency,
        int sent,
        bool timedOut,
        double loadStart,
        double loadEnd,
        RunResults results,
        IReadOnlyList<StatsRow> stats,
        string appContainer,
        string serverContainer,
        JsonElement appInspect,
        Dictionary<string, object> problems)
    {
        Dictionary<string, object> Usage(string container)
        {
            var all = stats.Where(row => row.Container == container).ToList();
            var during = all.Where(row => row.ElapsedSeconds >= loadStart && row.ElapsedSeconds <= loadEnd).ToList();
            var before = all.Where(row => row.ElapsedSeconds < loadStart).ToList();
            var after = all.Where(row => row.ElapsedSeconds > loadEnd).ToList();
            static double MiB(long bytes) => Math.Round(bytes / (1024.0 * 1024.0), 1);
            return new Dictionary<string, object>
            {
                ["samples"] = all.Count,
                ["memory_before_load_mib"] = before.Count == 0 ? 0 : MiB(before[^1].MemoryBytes),
                ["memory_max_during_load_mib"] = during.Count == 0 ? 0 : MiB(during.Max(static row => row.MemoryBytes)),
                ["memory_mean_during_load_mib"] = during.Count == 0 ? 0 : MiB((long)during.Average(static row => row.MemoryBytes)),
                ["memory_after_load_mib"] = after.Count == 0 ? 0 : MiB(after[^1].MemoryBytes),
                ["memory_limit_mib"] = all.Count == 0 ? 0 : MiB(all[^1].MemoryLimitBytes),
                ["cpu_max_percent"] = during.Count == 0 ? 0 : Math.Round(during.Max(static row => row.CpuPercent), 1),
                ["cpu_mean_percent"] = during.Count == 0 ? 0 : Math.Round(during.Average(static row => row.CpuPercent), 1),
                ["pids_max"] = all.Count == 0 ? 0 : all.Max(static row => row.Pids),
            };
        }

        var seconds = loadEnd - loadStart;
        var hostConfig = appInspect.GetProperty("HostConfig");
        return new Dictionary<string, object?>
        {
            ["mode"] = mode,
            ["finished_at"] = DateTimeOffset.UtcNow,
            ["runs_requested"] = runs,
            ["runs_sent"] = sent,
            ["in_flight_target"] = concurrency,
            ["timed_out"] = timedOut,
            ["load_duration_s"] = Math.Round(seconds, 1),
            ["parent_runs_per_s"] = Math.Round(results.Parents / seconds, 1),
            ["all_runs_per_s"] = Math.Round((results.Parents + results.Children) / seconds, 1),
            ["parent_runs"] = results.Parents,
            ["parent_runs_correct"] = results.ParentsCorrect,
            ["child_runs"] = results.Children,
            ["child_runs_correct"] = results.ChildrenCorrect,
            ["child_runs_per_account"] = results.ChildAccounts,
            ["success_rate"] = results.Parents + results.Children == 0 ? 0 : Math.Round((results.ParentsCorrect + results.ChildrenCorrect) / (double)(results.Parents + results.Children), 6),
            ["statuses"] = results.Statuses,
            ["parent_latency_queued_to_ended_ms"] = Percentiles.Of(results.ParentLatencies),
            ["parent_latency_sent_to_ended_ms"] = Percentiles.Of(results.ParentEndToEnd),
            ["child_latency_queued_to_ended_ms"] = Percentiles.Of(results.ChildLatencies),
            ["app_container"] = new Dictionary<string, object?>
            {
                ["image"] = appInspect.GetProperty("Config").GetProperty("Image").GetString(),
                ["memory_limit_bytes"] = hostConfig.GetProperty("Memory").GetInt64(),
                ["cpu_limit"] = hostConfig.GetProperty("NanoCpus").GetInt64() / 1e9,
                ["usage"] = Usage(appContainer),
            },
            ["server_container"] = new Dictionary<string, object?> { ["usage"] = Usage(serverContainer) },
            ["logs"] = problems,
        };
    }

    private sealed class RunResults
    {
        public List<string> Rows { get; } = [];

        public int Parents { get; private set; }

        public int ParentsCorrect { get; private set; }

        public int Children { get; private set; }

        public int ChildrenCorrect { get; private set; }

        public Dictionary<string, int> ChildAccounts { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, int> Statuses { get; } = new(StringComparer.Ordinal);

        public List<double> ParentLatencies { get; } = [];

        public List<double> ParentEndToEnd { get; } = [];

        public List<double> ChildLatencies { get; } = [];

        public bool AllCorrect => ParentsCorrect == Parents && ChildrenCorrect == Children && Children == 3 * Parents;

        public void Add(bool isParent, bool correct, string status, string output, TimeSpan? queuedToEnded, TimeSpan? sentToEnded)
        {
            Statuses[status] = Statuses.GetValueOrDefault(status) + 1;
            if (isParent)
            {
                Parents++;
                ParentsCorrect += correct ? 1 : 0;
                if (queuedToEnded is { } latency)
                {
                    ParentLatencies.Add(latency.TotalMilliseconds);
                }

                if (sentToEnded is { } endToEnd)
                {
                    ParentEndToEnd.Add(endToEnd.TotalMilliseconds);
                }

                return;
            }

            Children++;
            ChildrenCorrect += correct ? 1 : 0;
            if (correct)
            {
                ChildAccounts[ChildOutputs[output]] = ChildAccounts.GetValueOrDefault(ChildOutputs[output]) + 1;
            }

            if (queuedToEnded is { } childLatency)
            {
                ChildLatencies.Add(childLatency.TotalMilliseconds);
            }
        }
    }
}

internal static class Percentiles
{
    // Nearest-rank percentiles.
    public static Dictionary<string, double> Of(List<double> values)
    {
        if (values.Count == 0)
        {
            return [];
        }

        var sorted = values.Order().ToArray();
        double At(double percentile) => sorted[Math.Clamp((int)Math.Ceiling(percentile / 100 * sorted.Length) - 1, 0, sorted.Length - 1)];
        return new Dictionary<string, double>
        {
            ["count"] = sorted.Length,
            ["min"] = Math.Round(sorted[0]),
            ["p50"] = Math.Round(At(50)),
            ["p95"] = Math.Round(At(95)),
            ["p99"] = Math.Round(At(99)),
            ["max"] = Math.Round(sorted[^1]),
            ["mean"] = Math.Round(sorted.Average()),
        };
    }
}
