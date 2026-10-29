```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.7.1 (25G241) [Darwin 25.6.0]
Apple M3 Max, 1 CPU, 16 logical and 16 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), Arm64 RyuJIT armv8.0-a
  DefaultJob : .NET 10.0.12 (10.0.12, 10.0.1226.42308), Arm64 RyuJIT armv8.0-a


```
| Method          | BodyBytes | Mean       | Error     | StdDev    | Gen0   | Allocated |
|---------------- |---------- |-----------:|----------:|----------:|-------:|----------:|
| **VerifySignature** | **1024**      |   **1.182 μs** | **0.0055 μs** | **0.0051 μs** | **0.0381** |     **320 B** |
| **VerifySignature** | **1048576**   | **340.642 μs** | **2.6081 μs** | **2.0362 μs** |      **-** |     **320 B** |
