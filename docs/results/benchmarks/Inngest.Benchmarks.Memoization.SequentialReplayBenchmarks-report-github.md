```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.7.1 (25G241) [Darwin 25.6.0]
Apple M3 Max, 1 CPU, 16 logical and 16 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), Arm64 RyuJIT armv8.0-a
  DefaultJob : .NET 10.0.12 (10.0.12, 10.0.1226.42308), Arm64 RyuJIT armv8.0-a


```
| Method                | Steps | Mean       | Error      | StdDev     | Gen0    | Gen1    | Allocated |
|---------------------- |------ |-----------:|-----------:|-----------:|--------:|--------:|----------:|
| **ReplaySequentialSteps** | **1**     |   **5.529 μs** |  **0.0805 μs** |  **0.0714 μs** |  **0.8545** |  **0.0153** |   **6.98 KB** |
| **ReplaySequentialSteps** | **10**    |  **14.399 μs** |  **0.1271 μs** |  **0.1127 μs** |  **2.0752** |  **0.0610** |  **17.02 KB** |
| **ReplaySequentialSteps** | **100**   | **106.835 μs** |  **2.1170 μs** |  **2.4380 μs** | **14.1602** |  **2.4414** | **117.85 KB** |
| **ReplaySequentialSteps** | **500**   | **548.278 μs** | **10.8130 μs** | **12.0186 μs** | **66.4063** | **33.2031** | **556.42 KB** |
