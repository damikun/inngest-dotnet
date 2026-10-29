```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.7.1 (25G241) [Darwin 25.6.0]
Apple M3 Max, 1 CPU, 16 logical and 16 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), Arm64 RyuJIT armv8.0-a
  DefaultJob : .NET 10.0.12 (10.0.12, 10.0.1226.42308), Arm64 RyuJIT armv8.0-a


```
| Method       | Steps | Mean      | Error    | StdDev   | Gen0    | Gen1   | Allocated |
|------------- |------ |----------:|---------:|---------:|--------:|-------:|----------:|
| **DiscoverStep** | **10**    |  **15.28 μs** | **0.284 μs** | **0.279 μs** |  **2.1362** | **0.0916** |  **17.45 KB** |
| **DiscoverStep** | **100**   | **106.86 μs** | **2.128 μs** | **3.119 μs** | **14.1602** | **1.9531** | **118.29 KB** |
