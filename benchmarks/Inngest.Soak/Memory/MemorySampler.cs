using System.Diagnostics;
using System.Globalization;
using System.Runtime;

namespace Inngest.Soak.Memory;

// Samples the GC and the process at a fixed interval while a soak runs, and writes one CSV row per sample.
// Sizes per generation come from the last GC; gc_heap_bytes is what the heap holds right now, garbage included.
internal sealed class MemorySampler : IAsyncDisposable
{
    public const string Header =
        "elapsed_s,invocations,invocations_per_s,gc_heap_bytes,heap_after_last_gc_bytes,gen0_bytes,gen1_bytes,gen2_bytes," +
        "loh_bytes,poh_bytes,fragmented_bytes,committed_bytes,working_set_bytes,gen0_collections,gen1_collections," +
        "gen2_collections,allocated_bytes,allocation_rate_mb_per_s,allocated_per_invocation_bytes,gc_pause_percent,threadpool_threads";

    private readonly Func<long> _invocations;
    private readonly StreamWriter _csv;
    private readonly PeriodicTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Task _loop;
    private long _lastInvocations;
    private long _lastAllocated;
    private TimeSpan _lastElapsed;

    public MemorySampler(string path, TimeSpan interval, Func<long> invocations)
    {
        _invocations = invocations;
        _csv = new StreamWriter(path) { AutoFlush = true };
        _csv.WriteLine(Header);
        _lastAllocated = GC.GetTotalAllocatedBytes(precise: false);
        _timer = new PeriodicTimer(interval);
        Write();
        _loop = LoopAsync();
    }

    public TimeSpan Elapsed => _clock.Elapsed;

    public async ValueTask DisposeAsync()
    {
        _timer.Dispose();
        await _loop.ConfigureAwait(false);
        Write();
        await _csv.DisposeAsync().ConfigureAwait(false);
    }

    private async Task LoopAsync()
    {
        while (await _timer.WaitForNextTickAsync().ConfigureAwait(false))
        {
            Write();
        }
    }

    private void Write()
    {
        var elapsed = _clock.Elapsed;
        var invocations = _invocations();
        var allocated = GC.GetTotalAllocatedBytes(precise: false);
        var info = GC.GetGCMemoryInfo(GCKind.Any);
        var seconds = Math.Max((elapsed - _lastElapsed).TotalSeconds, 1e-9);
        var newInvocations = invocations - _lastInvocations;
        var newBytes = allocated - _lastAllocated;

        _csv.WriteLine(string.Join(',',
            F(elapsed.TotalSeconds, "0.000"),
            invocations,
            F(newInvocations / seconds, "0"),
            GC.GetTotalMemory(forceFullCollection: false),
            info.HeapSizeBytes,
            info.GenerationInfo[0].SizeAfterBytes,
            info.GenerationInfo[1].SizeAfterBytes,
            info.GenerationInfo[2].SizeAfterBytes,
            info.GenerationInfo[3].SizeAfterBytes,
            info.GenerationInfo[4].SizeAfterBytes,
            info.FragmentedBytes,
            info.TotalCommittedBytes,
            Environment.WorkingSet,
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            allocated,
            F(newBytes / seconds / (1024 * 1024), "0.0"),
            newInvocations > 0 ? F((double)newBytes / newInvocations, "0") : string.Empty,
            F(info.PauseTimePercentage, "0.00"),
            ThreadPool.ThreadCount));

        _lastElapsed = elapsed;
        _lastInvocations = invocations;
        _lastAllocated = allocated;
    }

    private static string F(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);
}

// The heap after full, compacting, blocking collections at a moment when no invocation is running: what the process
// keeps. If invocations leaked, this would grow with their number.
internal sealed record Checkpoint(int Index, long Invocations, double ElapsedSeconds, long HeapBytes, long Gen2Bytes, long LohBytes, long PohBytes, long CommittedBytes, long WorkingSetBytes)
{
    public const string Header = "checkpoint,invocations,elapsed_s,heap_after_forced_gc_bytes,gen2_bytes,loh_bytes,poh_bytes,committed_bytes,working_set_bytes";

    public static Checkpoint Take(int index, long invocations, TimeSpan elapsed)
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        var info = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        return new Checkpoint(
            index,
            invocations,
            elapsed.TotalSeconds,
            GC.GetTotalMemory(forceFullCollection: false),
            info.GenerationInfo[2].SizeAfterBytes,
            info.GenerationInfo[3].SizeAfterBytes,
            info.GenerationInfo[4].SizeAfterBytes,
            info.TotalCommittedBytes,
            Environment.WorkingSet);
    }

    public string ToCsv() => string.Join(',',
        Index, Invocations, ElapsedSeconds.ToString("0.000", CultureInfo.InvariantCulture), HeapBytes, Gen2Bytes, LohBytes, PohBytes, CommittedBytes, WorkingSetBytes);
}
