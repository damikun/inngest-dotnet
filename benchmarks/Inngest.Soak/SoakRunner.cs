using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Inngest.Soak.Memory;
using Inngest.Soak.Workload;

namespace Inngest.Soak;

// One invocation: runs the call and returns null when the SDK answered as expected, or what went wrong.
internal delegate ValueTask<string?> Invoke(SoakCall call, bool checkBody, CancellationToken cancellationToken);

// Runs the invocations in segments with a bounded number in flight, takes a checkpoint after each segment, and
// writes <name>-samples.csv, <name>-checkpoints.csv and <name>-summary.json.
internal static class SoakRunner
{
    // Distinct runs whose calls the invocations cycle through, so that no two neighbouring calls carry the same body.
    private const int Runs = 128;

    public static async Task<int> RunAsync(string name, CommandLine options, int defaultInvocations, Invoke invoke, CancellationToken cancellationToken)
    {
        var segments = options.GetInt("checkpoints", 20);
        var perSegment = options.GetInt("invocations", defaultInvocations) / segments / CallShapes.CallsPerRun * CallShapes.CallsPerRun;
        var invocations = (long)perSegment * segments;
        var concurrency = options.GetInt("concurrency", 64);
        var output = options.Get("out", "docs/results/soak");
        Directory.CreateDirectory(output);

        var calls = Enumerable.Range(0, Runs).SelectMany(CallShapes.ForRun).ToArray();
        var executedBefore = StepTimingMiddleware.Executed;
        var sendsBefore = EventApiStub.Sends;
        long completed = 0;
        long failed = 0;
        string? firstFailure = null;
        var checkpoints = new List<Checkpoint>();
        var collectionsBefore = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);

        Console.WriteLine($"{name}: {invocations:N0} invocations, {concurrency} in flight, {segments} checkpoints");
        TimeSpan duration;
        await using (var sampler = new MemorySampler(Path.Combine(output, $"{name}-samples.csv"), TimeSpan.FromSeconds(1), () => Interlocked.Read(ref completed)))
        {
            checkpoints.Add(Checkpoint.Take(0, 0, sampler.Elapsed));
            for (var segment = 0; segment < segments; segment++)
            {
                await Parallel.ForEachAsync(
                    Enumerable.Range(segment * perSegment, perSegment),
                    new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = cancellationToken },
                    async (index, token) =>
                    {
                        var call = calls[index % calls.Length];

                        // Every answer's status is checked; whole bodies for the first two runs and then a sample, so
                        // that parsing them does not dominate what is measured.
                        var error = await invoke(call, index < 2 * CallShapes.CallsPerRun || index % 101 == 0, token).ConfigureAwait(false);
                        if (error is not null && Interlocked.Increment(ref failed) == 1)
                        {
                            firstFailure = $"{call.Shape}: {error}";
                        }

                        Interlocked.Increment(ref completed);
                    }).ConfigureAwait(false);

                var checkpoint = Checkpoint.Take(segment + 1, Interlocked.Read(ref completed), sampler.Elapsed);
                checkpoints.Add(checkpoint);
                Console.WriteLine($"  {checkpoint.Invocations,10:N0} invocations  {sampler.Elapsed.TotalSeconds,7:0.0} s  heap after forced GC {checkpoint.HeapBytes / 1024.0,9:N0} KB  failed {Interlocked.Read(ref failed)}");
            }

            duration = sampler.Elapsed;
        }

        await File.WriteAllLinesAsync(
            Path.Combine(output, $"{name}-checkpoints.csv"),
            [Checkpoint.Header, .. checkpoints.Select(static checkpoint => checkpoint.ToCsv())],
            cancellationToken).ConfigureAwait(false);

        // Two forced collections per checkpoint are left out of the counts of the soak itself.
        var forced = 2 * checkpoints.Count;
        var summary = new Dictionary<string, object?>
        {
            ["soak"] = name,
            ["finished_at"] = DateTimeOffset.UtcNow,
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["os"] = RuntimeInformation.OSDescription,
            ["processor_count"] = Environment.ProcessorCount,
            ["server_gc"] = GCSettings.IsServerGC,
            ["gc_latency_mode"] = GCSettings.LatencyMode.ToString(),
            ["invocations"] = invocations,
            ["concurrency"] = concurrency,
            ["duration_s"] = Math.Round(duration.TotalSeconds, 1),
            ["invocations_per_s"] = Math.Round(invocations / duration.TotalSeconds),
            ["failed"] = failed,
            ["first_failure"] = firstFailure,
            ["steps_executed"] = StepTimingMiddleware.Executed - executedBefore,
            ["steps_executed_expected"] = invocations / CallShapes.CallsPerRun * 5,
            ["events_sent"] = EventApiStub.Sends - sendsBefore,
            ["events_sent_expected"] = invocations / CallShapes.CallsPerRun,
            ["allocated_bytes_per_invocation"] = Math.Round((GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore) / (double)invocations),
            ["gen0_collections"] = GC.CollectionCount(0) - collectionsBefore.Item1 - forced,
            ["gen1_collections"] = GC.CollectionCount(1) - collectionsBefore.Item2 - forced,
            ["gen2_collections"] = GC.CollectionCount(2) - collectionsBefore.Item3 - forced,
            ["heap"] = Plateau(checkpoints),
        };

        await File.WriteAllTextAsync(
            Path.Combine(output, $"{name}-summary.json"),
            JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken).ConfigureAwait(false);

        Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        var complete = failed == 0
            && (long)summary["steps_executed"]! == (long)summary["steps_executed_expected"]!
            && (long)summary["events_sent"]! == (long)summary["events_sent_expected"]!;
        return complete ? 0 : 1;
    }

    // The heap after forced collections: at the start, after the first segment (warm), and over the second half of the
    // run, with the slope of a straight line through the second half. A leak shows as a positive slope that the range
    // of the second half cannot explain.
    private static Dictionary<string, object> Plateau(List<Checkpoint> checkpoints)
    {
        var secondHalf = checkpoints.Skip(checkpoints.Count / 2).ToList();
        var slope = Trend.Slope([.. secondHalf.Select(static checkpoint => ((double)checkpoint.Invocations, (double)checkpoint.HeapBytes))]);
        return new Dictionary<string, object>
        {
            ["before_first_invocation_bytes"] = checkpoints[0].HeapBytes,
            ["after_first_segment_bytes"] = checkpoints[1].HeapBytes,
            ["last_bytes"] = checkpoints[^1].HeapBytes,
            ["second_half_min_bytes"] = secondHalf.Min(static checkpoint => checkpoint.HeapBytes),
            ["second_half_max_bytes"] = secondHalf.Max(static checkpoint => checkpoint.HeapBytes),
            ["second_half_slope_bytes_per_million_invocations"] = Math.Round(slope * 1_000_000),
            ["max_working_set_bytes"] = checkpoints.Max(static checkpoint => checkpoint.WorkingSetBytes),
        };
    }

    public static string? Check(SoakCall call, int status, ReadOnlySpan<byte> body, bool checkBody)
    {
        if (status != call.ExpectedStatus)
        {
            return $"answered {status}, expected {call.ExpectedStatus}: {Encoding.UTF8.GetString(body)}";
        }

        return checkBody && SoakCall.Describe(status, body) is var actual && actual != call.Expected
            ? $"answered {actual}, expected {call.Expected}"
            : null;
    }
}
