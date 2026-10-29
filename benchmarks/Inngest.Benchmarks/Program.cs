using BenchmarkDotNet.Running;

var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

// A failed setup check leaves NA in the table. The exit code makes it fail a script as well.
return summaries.Any(summary => summary.HasCriticalValidationErrors || summary.Reports.Any(report => !report.Success)) ? 1 : 0;
