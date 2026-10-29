```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.7.1 (25G241) [Darwin 25.6.0]
Apple M3 Max, 1 CPU, 16 logical and 16 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), Arm64 RyuJIT armv8.0-a
  DefaultJob : .NET 10.0.12 (10.0.12, 10.0.1226.42308), Arm64 RyuJIT armv8.0-a


```
| Method              | Steps | Mean      | Error    | StdDev   | Gen0    | Gen1   | Allocated |
|-------------------- |------ |----------:|---------:|---------:|--------:|-------:|----------:|
| **ReplayParallelSteps** | **10**    |  **14.03 μs** | **0.131 μs** | **0.116 μs** |  **2.0752** | **0.0610** |  **16.97 KB** |
| **ReplayParallelSteps** | **100**   | **108.66 μs** | **2.158 μs** | **2.806 μs** | **14.1602** | **2.4414** |  **115.7 KB** |
