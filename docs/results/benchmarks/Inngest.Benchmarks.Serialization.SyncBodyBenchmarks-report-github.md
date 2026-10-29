```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.7.1 (25G241) [Darwin 25.6.0]
Apple M3 Max, 1 CPU, 16 logical and 16 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), Arm64 RyuJIT armv8.0-a
  DefaultJob : .NET 10.0.12 (10.0.12, 10.0.1226.42308), Arm64 RyuJIT armv8.0-a


```
| Method        | Functions | Mean      | Error     | StdDev    | Gen0   | Allocated |
|-------------- |---------- |----------:|----------:|----------:|-------:|----------:|
| **WriteSyncBody** | **1**         |  **1.240 μs** | **0.0135 μs** | **0.0105 μs** | **0.1431** |   **1.18 KB** |
| **WriteSyncBody** | **20**        | **22.536 μs** | **0.4382 μs** | **0.4500 μs** | **1.9836** |  **16.25 KB** |
