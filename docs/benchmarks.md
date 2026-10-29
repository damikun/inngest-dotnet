# Benchmarks

`benchmarks/Inngest.Benchmarks` measures the work the SDK does on every call from Inngest: parsing the request,
replaying memoized steps, writing the answer and checking the signature. It uses
[BenchmarkDotNet](https://benchmarkdotnet.org) and touches no network.

The figures below are indicative. They come from one run on a laptop that was busy with other work, so treat them as
orders of magnitude. A full benchmark run on dedicated hardware is coming.

| Work | Mean | Allocated |
|---|---:|---:|
| Run the first step of a run | 5.1 µs | 6.4 KB |
| Replay 10 memoized steps | 14.4 µs | 17 KB |
| Replay 100 memoized steps | 107 µs | 118 KB |
| Parse a call that carries 100 step results | 33.3 µs | 27.6 KB |
| Verify the signature of a 1 KB request | 1.18 µs | 320 B |
| Write the sync body of 20 functions | 22.5 µs | 16.3 KB |
| Send one event through the client | 1.49 µs | 3.8 KB |

Each memoized step adds about 1.1 µs and 1.1 KB on top of about 4.5 µs and 6 KB per call. A middleware that
overrides nothing adds 8 to 9% to a call.

## Running them

```bash
dotnet run -c Release --project benchmarks/Inngest.Benchmarks -- --filter '*' --job short
```

`--job short` runs the 22 cases in about two minutes; leave it out for BenchmarkDotNet's slower, more precise default
job. Run them in Release on an idle machine and compare results only with runs from the same machine.

The figures above used the default job on 2026-10-29: an Apple M3 Max, .NET 10.0.12, BenchmarkDotNet 0.15.8, with a
load average that fell from 29.6 to 3.2 during the run. The full reports are in
[`docs/results/benchmarks`](results/benchmarks).
