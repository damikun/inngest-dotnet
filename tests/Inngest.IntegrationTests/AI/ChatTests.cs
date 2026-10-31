using System.Text;
using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Evaluation.Harness;
using Inngest.IntegrationTests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.AI;

// The chat steps against the dev server, which stores what they save and replays the function from it.
public sealed class ChatTests(EvaluationEnvironment environment) : EvaluationScenario<ChatFixture>(environment)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    [Fact(Timeout = 120_000)]
    public async Task The_model_and_the_tool_run_once_however_often_Inngest_calls_the_function()
    {
        var run = await RunToEndAsync(ChatFunctions.Agent, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<string>().ShouldBe("It is sunny in Prague.");

        // Two rounds with the model and one tool call, saved by the server and read back from it on every later call.
        Observed.Named("model").Count.ShouldBe(2);
        Observed.Values<string>("tool").ShouldBe(["Prague"]);

        // The function itself ran again for each step it waited for: the model, the tool, the model, the sleep.
        Observed.Named("body").Count.ShouldBeGreaterThanOrEqualTo(4);
    }

    [Fact(Timeout = 120_000)]
    public async Task The_text_of_a_streaming_call_reaches_a_subscriber_chunk_by_chunk()
    {
        var topic = ChatFunctions.Tokens(TestId);
        var realtime = Fixture.App.Services.GetRequiredService<IInngestRealtime>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        await using var messages = realtime.SubscribeAsync(topic.Channel, [topic.Name], stop.Token).GetAsyncEnumerator(stop.Token);

        // A subscription has no way to say it is connected, so publish until it receives something.
        var first = messages.MoveNextAsync().AsTask();
        for (var attempt = 0; attempt < 50 && !first.IsCompleted; attempt++)
        {
            await realtime.PublishAsync(topic, "ready", Cancellation);
            await Task.WhenAny(first, Task.Delay(500, Cancellation));
        }

        (await first).ShouldBeTrue();

        var run = await RunToEndAsync(ChatFunctions.Streaming, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<string>().ShouldBe("Your order is on its way");
        var text = new StringBuilder();
        var chunks = 0;
        using var timeout = new CancellationTokenSource(Wait);
        while (await messages.MoveNextAsync().AsTask().WaitAsync(timeout.Token))
        {
            if (messages.Current.Kind == RealtimeMessageKind.StreamEnd)
            {
                break;
            }

            if (messages.Current.Kind == RealtimeMessageKind.StreamChunk)
            {
                text.Append(messages.Current.Text);
                chunks++;
            }
        }

        text.ToString().ShouldBe("Your order is on its way");
        chunks.ShouldBeGreaterThan(1);
        Observed.Named("model").Count.ShouldBe(1);
    }

    [Fact(Timeout = 150_000)]
    public async Task A_stream_the_model_failed_in_ends_where_it_stopped_and_the_retry_streams_again_as_a_new_stream()
    {
        var topic = ChatFunctions.Tokens(TestId);
        var realtime = Fixture.App.Services.GetRequiredService<IInngestRealtime>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        await using var messages = realtime.SubscribeAsync(topic.Channel, [topic.Name], stop.Token).GetAsyncEnumerator(stop.Token);
        var first = messages.MoveNextAsync().AsTask();
        for (var attempt = 0; attempt < 50 && !first.IsCompleted; attempt++)
        {
            await realtime.PublishAsync(topic, "ready", Cancellation);
            await Task.WhenAny(first, Task.Delay(500, Cancellation));
        }

        (await first).ShouldBeTrue();

        var run = await RunToEndAsync(ChatFunctions.StreamingFailure, new RunEvent(TestId), TimeSpan.FromSeconds(120));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<string>().ShouldBe("Your order is on its way");
        Observed.Named("model").Count.ShouldBe(2);

        // Inngest ends a stream whose request stopped early like one that finished, so the first stream has an end of its own,
        // and the retry publishes a second stream, with the run's id, whole.
        var streams = new List<(string? Id, string? RunId, StringBuilder Text, bool Ended)>();
        using var timeout = new CancellationTokenSource(Wait);
        while (streams.Count < 2 || !streams[1].Ended)
        {
            if (!await messages.MoveNextAsync().AsTask().WaitAsync(timeout.Token))
            {
                break;
            }

            var message = messages.Current;
            switch (message.Kind)
            {
                case RealtimeMessageKind.StreamStart:
                    streams.Add((message.StreamId, message.RunId, new StringBuilder(), false));
                    break;
                case RealtimeMessageKind.StreamChunk:
                    streams.Single(stream => stream.Id == message.StreamId).Text.Append(message.Text);
                    break;
                case RealtimeMessageKind.StreamEnd:
                    var index = streams.FindIndex(stream => stream.Id == message.StreamId);
                    streams[index] = streams[index] with { Ended = true };
                    break;
            }
        }

        streams.Count.ShouldBe(2);
        streams.Select(stream => stream.Id).Distinct().Count().ShouldBe(2);
        streams.ShouldAllBe(stream => stream.RunId == run.RunId && stream.Ended);
        streams[0].Text.ToString().ShouldBe("Your order ");
        streams[1].Text.ToString().ShouldBe("Your order is on its way");
    }
}
