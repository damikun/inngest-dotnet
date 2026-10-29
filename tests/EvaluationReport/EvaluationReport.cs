// Writes the results section of .internal/evaluation.md from the TRX report of an evaluation run:
//
//   dotnet run tests/EvaluationReport/EvaluationReport.cs -- <run.trx> .internal/evaluation.md
//
// The section between the results markers is replaced; without a document the section is printed instead. Machine
// details are read from the machine the script runs on, which should be the one that ran the evaluation.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

const string StartMarker = "<!-- results:start -->";
const string EndMarker = "<!-- results:end -->";

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: dotnet run tests/EvaluationReport/EvaluationReport.cs -- <run.trx> [.internal/evaluation.md]");
    return 2;
}

XNamespace trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
var run = XDocument.Load(args[0]).Root!;
var times = run.Element(trx + "Times")!;
var wallTime = DateTimeOffset.Parse(times.Attribute("finish")!.Value, CultureInfo.InvariantCulture)
    - DateTimeOffset.Parse(times.Attribute("start")!.Value, CultureInfo.InvariantCulture);

// Areas in the order the evaluation brief lists them, by the scenario classes that cover them.
(string Area, string[] Classes)[] areas =
[
    ("Triggers and events", ["TriggerScenarios"]),
    ("Steps", ["StepScenarios", "StepDataScenarios", "ParallelStepScenarios"]),
    ("Sleep and waitForEvent", ["SleepScenarios", "WaitForEventScenarios"]),
    ("Invoke", ["InvokeScenarios"]),
    ("sendEvent", ["SendEventScenarios"]),
    ("Errors and retries", ["RetryScenarios", "FailureScenarios"]),
    ("Flow control", ["ConcurrencyScenarios", "RateScenarios", "DebounceAndBatchScenarios", "SingletonAndTimeoutScenarios", "CancellationScenarios"]),
    ("Middleware and logging", ["MiddlewareScenarios"]),
    ("Durability", ["LoadScenarios", "LargeStateScenarios", "RestartScenarios"]),
    ("Connect", ["ConnectScenarios", "CloudConnectScenarios"]),
    ("Cloud mode", ["CloudScenarios", "RotationScenarios"]),
];

var results = run.Descendants(trx + "UnitTestResult")
    .Select(result => Scenario.From(
        result.Attribute("testName")!.Value,
        result.Attribute("outcome")!.Value,
        TimeSpan.Parse(result.Attribute("duration")!.Value, CultureInfo.InvariantCulture)))
    .Where(scenario => scenario.Namespace.EndsWith(".Evaluation", StringComparison.Ordinal))
    .Select(scenario =>
    {
        var index = Array.FindIndex(areas, area => area.Classes.Contains(scenario.Class));
        return (Scenario: scenario, AreaIndex: index, ClassIndex: index < 0 ? 0 : Array.IndexOf(areas[index].Classes, scenario.Class));
    })
    .OrderBy(item => item.AreaIndex < 0 ? int.MaxValue : item.AreaIndex)
    .ThenBy(item => item.ClassIndex)
    .ThenBy(item => item.Scenario.Name, StringComparer.Ordinal)
    .Select(item => (item.Scenario, Area: item.AreaIndex < 0 ? item.Scenario.Class : areas[item.AreaIndex].Area))
    .ToList();

var passed = results.Count(item => item.Scenario.Outcome == "Passed");
var failed = results.Count(item => item.Scenario.Outcome == "Failed");
var others = results.Count - passed - failed;
var started = DateTimeOffset.Parse(times.Attribute("start")!.Value, CultureInfo.InvariantCulture).ToUniversalTime();

var text = new StringBuilder();
text.AppendLine(CultureInfo.InvariantCulture, $"Run of {started:yyyy-MM-dd HH:mm} UTC: **{results.Count} scenarios, {passed} passed, {failed} failed, {others} not run**.");
text.AppendLine(CultureInfo.InvariantCulture, $"Wall time {Seconds(wallTime)}; the scenarios' own durations add up to {Seconds(TimeSpan.FromTicks(results.Sum(item => item.Scenario.Duration.Ticks)))}, because scenario classes run in parallel.");
text.AppendLine();
text.AppendLine(CultureInfo.InvariantCulture, $"Machine: {Machine()}.");
text.AppendLine();
text.AppendLine("| Area | Scenarios | Passed | Total time | Slowest |");
text.AppendLine("|---|---:|---:|---:|---:|");
foreach (var group in results.GroupBy(item => item.Area))
{
    text.AppendLine(CultureInfo.InvariantCulture,
        $"| {group.Key} | {group.Count()} | {group.Count(item => item.Scenario.Outcome == "Passed")} | {Seconds(TimeSpan.FromTicks(group.Sum(item => item.Scenario.Duration.Ticks)))} | {Seconds(group.Max(item => item.Scenario.Duration))} |");
}

text.AppendLine();
text.AppendLine("| Scenario | Category | Result | Duration |");
text.AppendLine("|---|---|---|---:|");
foreach (var (scenario, area) in results)
{
    text.AppendLine(CultureInfo.InvariantCulture, $"| {scenario.Name} | {area} | {scenario.Outcome} | {Seconds(scenario.Duration)} |");
}

var section = text.ToString();
if (args.Length == 1)
{
    Console.Write(section);
    return 0;
}

var document = File.ReadAllText(args[1]);
var start = document.IndexOf(StartMarker, StringComparison.Ordinal);
var end = document.IndexOf(EndMarker, StringComparison.Ordinal);
if (start < 0 || end < start)
{
    Console.Error.WriteLine($"{args[1]} has no {StartMarker} ... {EndMarker} section.");
    return 1;
}

File.WriteAllText(args[1], $"{document[..(start + StartMarker.Length)]}\n{section}{document[end..]}");
Console.WriteLine($"Wrote {results.Count} scenarios to {args[1]}.");
return 0;

static string Seconds(TimeSpan duration) => $"{duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s";

static string Machine()
{
    var cpu = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
        ? Run("sysctl", "-n machdep.cpu.brand_string")
        : File.Exists("/proc/cpuinfo")
            ? File.ReadLines("/proc/cpuinfo").FirstOrDefault(line => line.StartsWith("model name", StringComparison.Ordinal))?.Split(':', 2)[1].Trim()
            : null;
    var os = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
        ? $"macOS {Run("sw_vers", "-productVersion")}"
        : RuntimeInformation.OSDescription;
    var memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024);
    var docker = Run("docker", "version --format {{.Server.Version}}");
    return string.Join(", ",
    [
        cpu ?? RuntimeInformation.ProcessArchitecture.ToString(),
        $"{Environment.ProcessorCount} logical cores",
        $"{memory.ToString("0", CultureInfo.InvariantCulture)} GB of memory",
        $"{os} ({RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()})",
        $".NET {Environment.Version}",
        docker is null ? "Docker version unknown" : $"Docker {docker}",
    ]);
}

static string? Run(string file, string arguments)
{
    try
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments) { RedirectStandardOutput = true, RedirectStandardError = true });
        if (process is null)
        {
            return null;
        }

        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return process.ExitCode == 0 && output.Length > 0 ? output : null;
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return null;
    }
}

// A test case of the TRX: "Namespace.Class.Method_name(arguments)".
internal sealed record Scenario(string Namespace, string Class, string Name, string Outcome, TimeSpan Duration)
{
    public static Scenario From(string testName, string outcome, TimeSpan duration)
    {
        var arguments = testName.IndexOf('(', StringComparison.Ordinal);
        var path = arguments < 0 ? testName : testName[..arguments];
        var parts = path.Split('.');
        var name = parts[^1].Replace('_', ' ') + (arguments < 0 ? string.Empty : $" {testName[arguments..]}");
        return new Scenario(string.Join('.', parts[..^2]), parts[^2], name, outcome, duration);
    }
}
