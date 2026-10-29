using System.Diagnostics;
using System.Text.Json;

namespace Inngest.Soak.Docker;

// Runs every flow of samples/Inngest.Sample against the containerized server and app, and checks what came out.
// The tool only talks to the server's published API; the server and the app talk to each other over the Docker
// network. The checks that need to be on that network run inside the server's container.
internal static class FlowCheck
{
    private const string AppId = "inngest-dotnet-sample";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public static async Task<int> RunAsync(CommandLine options, CancellationToken cancellationToken)
    {
        var mode = options.Require("mode");
        var serverContainer = options.Require("server-container");
        var appContainer = options.Require("app-container");
        var output = options.Get("out", "docs/results/docker");
        Directory.CreateDirectory(output);
        using var server = new InngestServer(new Uri(options.Get("inngest", "http://127.0.0.1:18288")), options.Get("event-key", "dev-mode-accepts-any-key"));

        var results = new List<FlowResult>();
        async Task CheckAsync(string name, Func<Task<string>> check)
        {
            var clock = Stopwatch.StartNew();
            try
            {
                results.Add(new FlowResult(name, true, await check(), clock.Elapsed.TotalSeconds));
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                results.Add(new FlowResult(name, false, exception.Message, clock.Elapsed.TotalSeconds));
            }

            var last = results[^1];
            Console.WriteLine($"{(last.Passed ? "PASS" : "FAIL")}  {last.Name} ({last.Seconds:0.0} s): {last.Detail}");
        }

        await CheckAsync("the server is healthy", async () =>
            await server.IsHealthyAsync(cancellationToken) ? "GET /health answered 200" : throw new InvalidOperationException("GET /health did not answer 200"));

        await CheckAsync("the app synced over the Docker network", async () =>
        {
            var app = await PollAsync(
                () => server.FindAppAsync(AppId, cancellationToken),
                static app => app is { } found && found.GetProperty("error").ValueKind == JsonValueKind.Null && found.GetProperty("functionCount").GetInt32() == 5,
                "the app to sync with 5 functions",
                cancellationToken);
            var found = app!.Value;
            return $"{found.GetProperty("url").GetString()}, sdk {found.GetProperty("sdkLanguage").GetString()} {found.GetProperty("sdkVersion").GetString()}, " +
                $"framework {found.GetProperty("framework").GetString()}, method {found.GetProperty("method").GetString()}, {found.GetProperty("functionCount").GetInt32()} functions";
        });

        await CheckAsync("the app container runs as non-root, publishes no port and is not on the host network", async () =>
        {
            var inspect = await DockerCli.InspectAsync(appContainer, cancellationToken);
            var user = inspect.GetProperty("Config").GetProperty("User").GetString();
            var network = inspect.GetProperty("HostConfig").GetProperty("NetworkMode").GetString();
            var bindings = inspect.GetProperty("HostConfig").GetProperty("PortBindings");
            var published = bindings.ValueKind == JsonValueKind.Object ? bindings.EnumerateObject().Count() : 0;
            var health = inspect.GetProperty("State").GetProperty("Health").GetProperty("Status").GetString();
            var networks = string.Join(' ', inspect.GetProperty("NetworkSettings").GetProperty("Networks").EnumerateObject().Select(static entry => entry.Name));
            if (user is null or "" or "0" or "root" || network == "host" || published != 0 || health != "healthy")
            {
                throw new InvalidOperationException($"user '{user}', network mode {network}, {published} published ports, health {health}");
            }

            return $"user {user}, network mode {network} ({networks}), {published} published ports, health {health}";
        });

        if (mode == "cloud")
        {
            await CheckAsync("an unsigned GET from inside the network is refused", async () =>
                Expect(await DockerCli.HttpStatusLineFromAsync(serverContainer, "sample", 8080, "GET /api/inngest HTTP/1.1\r\nHost: sample:8080\r\nConnection: close\r\n\r\n", cancellationToken), "401"));

            await CheckAsync("a call with a forged signature from inside the network is refused", async () =>
            {
                var forged = $"t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}&s={new string('0', 64)}";
                var request =
                    $"POST /api/inngest?fnId={AppId}-hello-world&stepId=step HTTP/1.1\r\nHost: sample:8080\r\nContent-Type: application/json\r\n" +
                    $"X-Inngest-Signature: {forged}\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{{}}";
                return Expect(await DockerCli.HttpStatusLineFromAsync(serverContainer, "sample", 8080, request, cancellationToken), "401");
            });

            await CheckAsync("the server refuses events with another key", async () =>
            {
                try
                {
                    await server.SendAsync([new { name = "demo/hello", data = new { name = "nobody" } }], cancellationToken, key: "not-the-event-key");
                }
                catch (InngestServerException exception) when (exception.StatusCode == 401)
                {
                    return "401 for an unknown event key";
                }

                throw new InvalidOperationException("the server accepted an unknown event key");
            });
        }
        else
        {
            await CheckAsync("introspection from inside the network answers in dev mode", async () =>
                Expect(await DockerCli.HttpStatusLineFromAsync(serverContainer, "sample", 8080, "GET /api/inngest HTTP/1.1\r\nHost: sample:8080\r\nConnection: close\r\n\r\n", cancellationToken), "200"));
        }

        await CheckAsync("hello-world runs its step and returns the greeting", async () =>
        {
            var run = await RunOfEventAsync(server, new { name = "demo/hello", data = new { name = "Docker" } }, $"{AppId}-hello-world", cancellationToken);
            return ExpectOutput(run, """{"greeting":"Hello, Docker!"}""");
        });

        await CheckAsync("summarize-accounts invokes score-account three times in parallel and sends an event", async () =>
        {
            var from = DateTimeOffset.UtcNow.AddSeconds(-1);
            var run = await RunOfEventAsync(server, new { name = "sample/summary.requested", data = new { } }, $"{AppId}-summarize-accounts", cancellationToken);
            ExpectOutput(run, """{"accounts":3,"total":170}""");

            var functions = await server.FunctionIdsAsync(cancellationToken);
            var children = new List<string>();
            await foreach (var child in server.ListRunsAsync(from, [functions[$"{AppId}-score-account"]], cancellationToken))
            {
                var finished = await server.FunctionRunAsync(child.Id, cancellationToken);
                children.Add($"{finished.Status} {Canonical(finished.Output)}");
            }

            children.Sort(StringComparer.Ordinal);
            string[] expected =
            [
                """COMPLETED {"accountId":"acme","score":40}""",
                """COMPLETED {"accountId":"globex","score":60}""",
                """COMPLETED {"accountId":"initech","score":70}""",
            ];
            if (!children.SequenceEqual(expected))
            {
                throw new InvalidOperationException($"invoked runs: {string.Join("; ", children)}");
            }

            // The server stores an event a moment after it accepted it.
            var sent = await PollAsync(
                async () =>
                {
                    var ready = await server.QueryAsync(
                        "query($from: Time!) { eventsV2(first: 5, filter: {from: $from, eventNames: [\"sample/summary.ready\"]}) { edges { node { raw } } } }",
                        new { from },
                        cancellationToken);
                    return ready.GetProperty("eventsV2").GetProperty("edges").EnumerateArray()
                        .Select(static edge => JsonDocument.Parse(edge.GetProperty("node").GetProperty("raw").GetString()!).RootElement.GetProperty("data").GetRawText())
                        .ToList();
                },
                static events => events.Count > 0,
                "the app's sample/summary.ready event",
                cancellationToken);
            if (sent is not ["""{"total":170}"""])
            {
                throw new InvalidOperationException($"sample/summary.ready events: {string.Join("; ", sent)}");
            }

            return $"output {run.Output}; {string.Join("; ", children)}; the app sent sample/summary.ready {sent[0]}";
        });

        await CheckAsync("onboard-user waits for activation, sleeps 5 s and finishes", async () =>
        {
            var userId = $"user-{Guid.NewGuid():N}";
            var run = await StartedRunAsync(server, new { name = "sample/user.signed-up", data = new { userId, email = $"{userId}@example.com" } }, $"{AppId}-onboard-user", cancellationToken);
            await WaitForSpanAsync(server, run.Id, "wait-for-activation", "WAITING", cancellationToken);
            await server.SendAsync([new { name = "sample/user.activated", data = new { userId } }], cancellationToken);
            var finished = await FinishedAsync(server, run.Id, cancellationToken);
            using var outputJson = JsonDocument.Parse(finished.Output ?? "null");
            var root = outputJson.RootElement;
            if (finished.Status != "COMPLETED" || root.GetProperty("userId").GetString() != userId || !root.GetProperty("activated").GetBoolean() ||
                root.GetProperty("welcomeId").GetString() is not { Length: 32 })
            {
                throw new InvalidOperationException($"{finished.Status} {finished.Output}");
            }

            return $"{finished.Status} after {(finished.EndedAt - finished.QueuedAt)?.TotalSeconds:0.0} s: {finished.Output}";
        });

        await CheckAsync("onboard-user is cancelled by sample/user.deleted", async () =>
        {
            var userId = $"user-{Guid.NewGuid():N}";
            var run = await StartedRunAsync(server, new { name = "sample/user.signed-up", data = new { userId, email = $"{userId}@example.com" } }, $"{AppId}-onboard-user", cancellationToken);
            await WaitForSpanAsync(server, run.Id, "wait-for-activation", "WAITING", cancellationToken);
            await server.SendAsync([new { name = "sample/user.deleted", data = new { userId } }], cancellationToken);
            var finished = await FinishedAsync(server, run.Id, cancellationToken);
            return finished.Status == "CANCELLED" ? "CANCELLED while it waited for activation" : throw new InvalidOperationException(finished.Status);
        });

        // The two requests refused on purpose in cloud mode are the only warnings allowed.
        await CheckAsync("the app logged the sync and no other warnings or errors", async () =>
        {
            var lines = await DockerCli.LogsAsync(appContainer, cancellationToken);
            var synced = lines.FirstOrDefault(static line => line.Contains("Synced 5 functions", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("no sync in the app's log");
            var problems = lines.Where(static line => line.Contains(" warn: ", StringComparison.Ordinal) || line.Contains(" fail: ", StringComparison.Ordinal) || line.Contains(" crit: ", StringComparison.Ordinal)).ToList();
            var refused = problems.Count(static line => line.Contains("[401] Rejected an Inngest ", StringComparison.Ordinal));
            var expected = mode == "cloud" ? 2 : 0;
            return problems.Count == expected && refused == expected
                ? $"{synced.Trim()}; {refused} warnings for the refused probes"
                : throw new InvalidOperationException(string.Join(" | ", problems.Take(5)));
        });

        var path = Path.Combine(output, $"{mode}-flows.json");
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(new { mode, checkedAt = DateTimeOffset.UtcNow, server = server.BaseUrl, results }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
            cancellationToken);
        Console.WriteLine($"{results.Count(static result => result.Passed)} of {results.Count} checks passed; wrote {path}");
        return results.TrueForAll(static result => result.Passed) ? 0 : 1;
    }

    private static string Expect(string statusLine, string status) =>
        statusLine.StartsWith($"HTTP/1.1 {status} ", StringComparison.Ordinal) ? statusLine : throw new InvalidOperationException($"answered '{statusLine}', expected {status}");

    private static string ExpectOutput(RunInfo run, string expected) =>
        run.Status == "COMPLETED" && Canonical(run.Output) == expected
            ? $"{run.Status} in {(run.EndedAt - run.QueuedAt)?.TotalMilliseconds:0} ms: {run.Output}"
            : throw new InvalidOperationException($"{run.Status} {run.Output}, expected {expected}");

    // Inngest stores outputs with sorted keys; this sorts them too, so that outputs compare as text.
    public static string Canonical(string? json)
    {
        if (json is null)
        {
            return "null";
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object
            ? "{" + string.Join(',', document.RootElement.EnumerateObject().OrderBy(static property => property.Name, StringComparer.Ordinal)
                .Select(static property => $"{JsonSerializer.Serialize(property.Name)}:{property.Value.GetRawText()}")) + "}"
            : document.RootElement.GetRawText();
    }

    private static async Task<RunInfo> RunOfEventAsync(InngestServer server, object @event, string functionSlug, CancellationToken cancellationToken)
    {
        var run = await StartedRunAsync(server, @event, functionSlug, cancellationToken);
        return await FinishedAsync(server, run.Id, cancellationToken);
    }

    private static async Task<RunInfo> StartedRunAsync(InngestServer server, object @event, string functionSlug, CancellationToken cancellationToken)
    {
        var eventId = (await server.SendAsync([@event], cancellationToken))[0];
        var runs = await PollAsync(
            () => server.EventRunsAsync(eventId, cancellationToken),
            runs => runs.Any(run => run.FunctionSlug == functionSlug),
            $"a run of {functionSlug} for event {eventId}",
            cancellationToken);
        return runs.First(run => run.FunctionSlug == functionSlug);
    }

    // A run counts as finished once it has ended, and a completed one once its output is stored as well.
    public static Task<RunInfo> FinishedAsync(InngestServer server, string runId, CancellationToken cancellationToken) =>
        PollAsync(
            () => server.FunctionRunAsync(runId, cancellationToken),
            static run => run.IsFinished && (run.Status != "COMPLETED" || run.Output is not null),
            $"run {runId} to finish",
            cancellationToken);

    private static Task WaitForSpanAsync(InngestServer server, string runId, string step, string status, CancellationToken cancellationToken) =>
        PollAsync(
            () => server.RunAsync(runId, cancellationToken),
            found => found.Spans.ValueKind == JsonValueKind.Array &&
                found.Spans.EnumerateArray().Any(span => span.GetProperty("name").GetString() == step && span.GetProperty("status").GetString() == status),
            $"step {step} of run {runId} to be {status}",
            cancellationToken);

    private static async Task<T> PollAsync<T>(Func<Task<T>> read, Func<T, bool> done, string what, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var value = await read();
            if (done(value))
            {
                return value;
            }

            if (clock.Elapsed > Timeout)
            {
                throw new TimeoutException($"Waited {Timeout.TotalSeconds:0} s for {what}.");
            }

            await Task.Delay(200, cancellationToken);
        }
    }

    private sealed record FlowResult(string Name, bool Passed, string Detail, double Seconds);
}
