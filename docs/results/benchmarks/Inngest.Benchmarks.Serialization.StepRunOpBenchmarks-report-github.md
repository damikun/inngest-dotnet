```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.7.1 (25G241) [Darwin 25.6.0]
Apple M3 Max, 1 CPU, 16 logical and 16 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), Arm64 RyuJIT armv8.0-a
  DefaultJob : .NET 10.0.12 (10.0.12, 10.0.1226.42308), Arm64 RyuJIT armv8.0-a


```
| Method         | Mean     | Error   | StdDev  | Gen0   | Allocated |
|--------------- |---------:|--------:|--------:|-------:|----------:|
| WriteStepRunOp | 279.7 ns | 1.44 ns | 1.20 ns | 0.0200 |     168 B |
