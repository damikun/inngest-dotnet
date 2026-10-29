using System.Globalization;

namespace Inngest.Soak;

// "--name value" pairs. Every command documents its own options in Usage.
internal sealed class CommandLine
{
    public const string Usage =
        """
        Usage: Inngest.Soak <command> [--option value ...]

          engine   Runs invocations against the execution engine directly and samples memory.
                   --invocations 2000000  --concurrency 64  --checkpoints 20  --out docs/results/soak
          http     Runs invocations through Kestrel and MapInngest in cloud mode (signed) and samples memory.
                   --invocations 400000  --concurrency 64  --checkpoints 20  --out docs/results/soak
          abandon  Checks with weak references that invocations abandoned on a pending step are collected.
                   --invocations 5000  --out docs/results/soak
          flows    Runs the sample app's flows against a containerized Inngest server and checks the results.
                   --inngest http://127.0.0.1:18288  --event-key <key>  --mode dev|cloud
                   --server-container <name>  --app-container <name>  --out docs/results/docker
          load     Drives the containerized sample app with summarize-accounts runs and records latencies,
                   docker stats and log errors.
                   --inngest http://127.0.0.1:18288  --event-key <key>  --mode dev|cloud  --runs 2000
                   --concurrency 300  --server-container <name>  --app-container <name>  --out docs/results/load
        """;

    private readonly Dictionary<string, string> _values;

    private CommandLine(Dictionary<string, string> values) => _values = values;

    public static CommandLine Parse(ReadOnlySpan<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
            {
                throw new ArgumentException($"Expected '--name value', but got '{args[i]}'.\n\n{Usage}");
            }

            values[args[i][2..]] = args[++i];
        }

        return new CommandLine(values);
    }

    public string Get(string name, string fallback) => _values.GetValueOrDefault(name, fallback);

    public string Require(string name) =>
        _values.TryGetValue(name, out var value) ? value : throw new ArgumentException($"--{name} is required.\n\n{Usage}");

    public int GetInt(string name, int fallback) =>
        _values.TryGetValue(name, out var value) ? int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture) : fallback;
}
