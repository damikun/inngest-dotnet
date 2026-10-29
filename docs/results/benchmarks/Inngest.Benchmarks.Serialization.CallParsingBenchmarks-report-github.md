```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.7.1 (25G241) [Darwin 25.6.0]
Apple M3 Max, 1 CPU, 16 logical and 16 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), Arm64 RyuJIT armv8.0-a
  DefaultJob : .NET 10.0.12 (10.0.12, 10.0.1226.42308), Arm64 RyuJIT armv8.0-a


```
| Method    | Steps | Mean      | Error     | StdDev    | Gen0   | Gen1   | Allocated |
|---------- |------ |----------:|----------:|----------:|-------:|-------:|----------:|
| **ParseCall** | **10**    |  **5.569 μs** | **0.0951 μs** | **0.0890 μs** | **0.4044** |      **-** |   **3.34 KB** |
| **ParseCall** | **100**   | **33.274 μs** | **0.2042 μs** | **0.1594 μs** | **3.3569** | **0.2441** |  **27.56 KB** |
