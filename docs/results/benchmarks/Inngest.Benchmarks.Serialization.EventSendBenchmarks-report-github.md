```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.7.1 (25G241) [Darwin 25.6.0]
Apple M3 Max, 1 CPU, 16 logical and 16 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), Arm64 RyuJIT armv8.0-a
  DefaultJob : .NET 10.0.12 (10.0.12, 10.0.1226.42308), Arm64 RyuJIT armv8.0-a


```
| Method     | Events | Mean      | Error     | StdDev    | Gen0   | Allocated |
|----------- |------- |----------:|----------:|----------:|-------:|----------:|
| **SendEvents** | **1**      |  **1.487 μs** | **0.0150 μs** | **0.0133 μs** | **0.4654** |    **3.8 KB** |
| **SendEvents** | **100**    | **62.429 μs** | **0.6815 μs** | **0.6375 μs** | **8.7891** |   **71.8 KB** |
