using System.Globalization;
using Inngest.Soak;
using Inngest.Soak.Docker;

// Long-running checks that the benchmarks cannot make: memory over hundreds of thousands of invocations, whether
// abandoned invocations are collected, and the sample app under load in Docker. docs/readiness.md shows how each
// command was run and what it measured.
if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(CommandLine.Usage);
    return 0;
}

// The output is data: decimal points, whatever the machine's locale.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var options = CommandLine.Parse(args.AsSpan(1));
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

try
{
    return args[0] switch
    {
        "engine" => await EngineSoak.RunAsync(options, cancellation.Token),
        "http" => await HttpSoak.RunAsync(options, cancellation.Token),
        "abandon" => await AbandonCheck.RunAsync(options, cancellation.Token),
        "flows" => await FlowCheck.RunAsync(options, cancellation.Token),
        "load" => await LoadTest.RunAsync(options, cancellation.Token),
        _ => throw new ArgumentException($"Unknown command '{args[0]}'.\n\n{CommandLine.Usage}"),
    };
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}
