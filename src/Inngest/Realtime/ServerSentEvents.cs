// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Runtime.CompilerServices;
using System.Text;

namespace Inngest.Realtime;

// The data of each event of a text/event-stream, as the HTML standard defines the format: "data:" lines, joined with a
// newline when an event has several, end an event at a blank line; comments start with a colon and other fields are
// ignored. Inngest sends one JSON message per event and a comment as a keepalive every 15 seconds.
internal static class ServerSentEvents
{
    // Throws TimeoutException when the stream sends nothing, not even a keepalive, for idleTimeout.
    public static async IAsyncEnumerable<string> ReadDataAsync(
        Stream stream,
        TimeProvider time,
        TimeSpan idleTimeout,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        using var idle = new CancellationTokenSource(idleTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, idle.Token);
        var data = new StringBuilder();
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (idle.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Inngest sent nothing for {idleTimeout.TotalSeconds:0} seconds.");
            }

            if (line is null)
            {
                yield break;
            }

            idle.CancelAfter(idleTimeout);
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return data.ToString();
                    data.Clear();
                }

                continue;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            if (data.Length > 0)
            {
                data.Append('\n');
            }

            var value = line.AsSpan(5);
            data.Append(value.StartsWith(" ") ? value[1..] : value);
        }
    }
}
