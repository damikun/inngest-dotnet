```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.7.1 (25G241) [Darwin 25.6.0]
Apple M3 Max, 1 CPU, 16 logical and 16 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), Arm64 RyuJIT armv8.0-a
  DefaultJob : .NET 10.0.12 (10.0.12, 10.0.1226.42308), Arm64 RyuJIT armv8.0-a


```
| Method                   | Categories         | Mean       | Error     | StdDev    | Ratio | RatioSD | Gen0    | Gen1   | Allocated | Alloc Ratio |
|------------------------- |------------------- |-----------:|----------:|----------:|------:|--------:|--------:|-------:|----------:|------------:|
| ExecuteWithoutMiddleware | Execute a new step |   5.132 μs | 0.1023 μs | 0.1050 μs |  1.00 |    0.03 |  0.7629 |      - |   6.43 KB |        1.00 |
| ExecuteWithMiddleware    | Execute a new step |   5.578 μs | 0.0630 μs | 0.0559 μs |  1.09 |    0.02 |  0.9766 |      - |   8.16 KB |        1.27 |
|                          |                    |            |           |           |       |         |         |        |           |             |
| ReplayWithoutMiddleware  | Replay 100 steps   | 108.486 μs | 2.0805 μs | 2.3959 μs |  1.00 |    0.03 | 14.1602 | 2.4414 | 117.85 KB |        1.00 |
| ReplayWithMiddleware     | Replay 100 steps   | 116.838 μs | 2.0870 μs | 1.8501 μs |  1.08 |    0.03 | 22.9492 | 4.3945 | 191.02 KB |        1.62 |
