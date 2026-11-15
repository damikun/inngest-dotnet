using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Inngest.Realtime;
using Inngest.Tests.Client;
using Inngest.Tests.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Inngest.Tests.Realtime;

public sealed class RealtimeStreamTests
{
    private static readonly RealtimeTopic Tokens = new("chat:42", "tokens");

    [Fact]
    public async Task A_stream_is_posted_as_text_stream_and_the_whole_text_comes_back()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK);
        await using var provider = ClientServices.Build(handler, new FakeTimeProvider(DateTimeOffset.UnixEpoch));

        var text = await provider.GetRequiredService<IInngestRealtime>().PublishStreamAsync(Tokens, Pieces("Hel", "", "lo ", "wörld"));

        text.ShouldBe("Hello wörld");
        var request = handler.Single;
        request.Uri.AbsoluteUri.ShouldBe("http://localhost:8288/v1/realtime/publish?channel=chat%3A42&topic=tokens");
        request.Header("Content-Type").ShouldBe("text/stream");
        request.BodyText.ShouldBe("Hello wörld");
    }

    // A model's reply can take minutes, and the request lasts as long as the stream.
    [Fact]
    public async Task A_stream_that_outlasts_the_clients_timeout_is_sent_whole()
    {
        var inngest = new Receiving();
        await using var provider = ClientServices.Build(
            new FakeHttpHandler(),
            new FakeTimeProvider(DateTimeOffset.UnixEpoch),
            configureServices: services => services
                .AddHttpClient(InngestServiceCollectionExtensions.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => inngest)
                .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromMilliseconds(100)));

        var text = await provider.GetRequiredService<IInngestRealtime>().PublishStreamAsync(Tokens, Slow(), TestContext.Current.CancellationToken);

        text.ShouldBe("Hello world");
        inngest.Body.ShouldBe("Hello world");

        static async IAsyncEnumerable<string> Slow()
        {
            yield return "Hello";
            await Task.Delay(TimeSpan.FromMilliseconds(400));
            yield return " world";
        }
    }

    // A handler that sends the request again, such as a retry policy, would call the producer, such as a model, again.
    [Fact]
    public async Task A_stream_is_read_once_and_cannot_be_sent_again()
    {
        var reads = 0;
        var content = new TextStreamContent(Counted(), TimeProvider.System);

        await content.CopyToAsync(Stream.Null, TestContext.Current.CancellationToken);
        var again = await Should.ThrowAsync<InvalidOperationException>(content.CopyToAsync(Stream.Null, TestContext.Current.CancellationToken));

        again.Message.ShouldBe("A streamed publish is sent once: sending it again would read its chunks again.");
        reads.ShouldBe(1);

        async IAsyncEnumerable<string> Counted()
        {
            reads++;
            await Task.Yield();
            yield return "Hello";
        }
    }

    [Fact]
    public async Task Long_chunks_go_out_in_pieces_below_1KB_that_end_on_whole_characters()
    {
        // 300 four-byte emoji: 1,200 bytes whose surrogate pairs a cut by bytes or chars would split.
        var text = string.Concat(Enumerable.Repeat("😀", 300)) + "é";
        var writes = new RecordingStream();

        await new TextStreamContent(Pieces(text), TimeProvider.System).CopyToAsync(writes, TestContext.Current.CancellationToken);

        writes.Pieces.ShouldAllBe(piece => piece.Length <= 1024);
        writes.Pieces.Count.ShouldBe(2);
        string.Concat(writes.Pieces.Select(piece => new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(piece))).ShouldBe(text);
    }

    // Inngest relays 1,000 reads of a stream, and a model yields a few characters at a time.
    [Fact]
    public async Task Chunks_that_arrive_together_go_out_in_one_piece()
    {
        var writes = new RecordingStream();

        await new TextStreamContent(Pieces("Hel", "lo", " wörld"), new FakeTimeProvider()).CopyToAsync(writes, TestContext.Current.CancellationToken);

        writes.Pieces.Select(piece => Encoding.UTF8.GetString(piece)).ShouldBe(["Hello wörld"]);
    }

    [Fact]
    public async Task Text_goes_out_once_it_has_waited_100_ms_for_more()
    {
        var time = new TestTimeProvider(EngineHost.Now);
        var more = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new RecordingStream();

        var sending = new TextStreamContent(Paused(), time).CopyToAsync(writes, TestContext.Current.CancellationToken);
        await time.TimersCreated(1).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMilliseconds(99));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        writes.Pieces.ShouldBeEmpty();
        time.Advance(TimeSpan.FromMilliseconds(1));
        await Until(() => writes.Pieces.Count == 1);
        more.SetResult();
        await sending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        writes.Pieces.Select(piece => Encoding.UTF8.GetString(piece)).ShouldBe(["Hel", "lo"]);

        async IAsyncEnumerable<string> Paused()
        {
            yield return "Hel";
            await more.Task;
            yield return "lo";
        }
    }

    [Fact]
    public async Task Text_waits_a_second_once_the_stream_has_500_pieces()
    {
        var time = new RecordingTimeProvider(EngineHost.Now);
        var more = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var content = new TextStreamContent(Long(), time);

        var sending = content.CopyToAsync(Stream.Null, TestContext.Current.CancellationToken);
        await Until(() => time.Timers.Count == 1);
        more.SetResult();
        await sending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        content.Pieces.ShouldBe(501);
        time.Timers.ShouldBe([TimeSpan.FromSeconds(1)]);

        async IAsyncEnumerable<string> Long()
        {
            for (var i = 0; i < 500; i++)
            {
                yield return new string('x', 1024);
            }

            yield return "y";
            await more.Task;
        }
    }

    // Inngest stops reading a stream after 1,000 reads and closes the connection once 256 KB more have come, which would
    // fail the publish.
    [Fact]
    public async Task A_stream_that_outgrows_what_Inngest_relays_ends_its_request_there_with_a_warning()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK);
        await using var provider = ClientServices.Build(handler, new FakeTimeProvider(DateTimeOffset.UnixEpoch), configureServices: services => services.AddFakeLogging());

        var text = await provider.GetRequiredService<IInngestRealtime>().PublishStreamAsync(Tokens, Full(1001), TestContext.Current.CancellationToken);

        text.Length.ShouldBe(1001 * 1024);
        handler.Single.Body.Length.ShouldBe(1000 * 1024);
        var warning = provider.GetFakeLogCollector().GetSnapshot().Where(record => record.Id.Id == 354).ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldBe(
            "The stream to topic tokens of channel chat:42 has outgrown the 1000 pieces Inngest relays, so its request ended there and its subscribers do not receive the rest of the text. The publish still reads all of it and returns it.");
    }

    [Fact]
    public async Task The_text_after_the_pieces_Inngest_relays_is_read_once_the_request_has_ended()
    {
        var inngest = new Receiving();
        await using var provider = ClientServices.Build(
            new FakeHttpHandler(),
            new FakeTimeProvider(DateTimeOffset.UnixEpoch),
            configureServices: services => services
                .AddHttpClient(InngestServiceCollectionExtensions.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => inngest));

        var text = await provider.GetRequiredService<IInngestRealtime>().PublishStreamAsync(Tokens, Long(), TestContext.Current.CancellationToken);

        text.ShouldBe(new string('x', 1001 * 1024) + " and the rest");
        inngest.Body.ShouldBe(new string('x', 1000 * 1024));

        async IAsyncEnumerable<string> Long()
        {
            await foreach (var chunk in Full(1001))
            {
                yield return chunk;
            }

            await inngest.Received.WaitAsync(TimeSpan.FromSeconds(10));
            yield return " and the rest";
        }
    }

    // The piece past the limit can be one whose text waited for more, while the next chunk is on its way.
    [Fact]
    public async Task Text_that_waited_past_the_last_piece_Inngest_relays_is_returned_after_the_request()
    {
        var time = new TestTimeProvider(EngineHost.Now);
        var more = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new RecordingStream();
        var content = new TextStreamContent(Long(), time);

        var sending = content.CopyToAsync(writes, TestContext.Current.CancellationToken);
        await time.TimersCreated(1).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(1));
        await sending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        more.SetResult();
        var text = await content.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        writes.Pieces.Count.ShouldBe(1000);
        text.ShouldBe(new string('x', 1000 * 1024) + "ab");

        async IAsyncEnumerable<string> Long()
        {
            for (var i = 0; i < 1000; i++)
            {
                yield return new string('x', 1024);
            }

            yield return "a";
            await more.Task;
            yield return "b";
        }
    }

    [Fact]
    public async Task A_publish_that_fails_after_the_pieces_Inngest_relays_stops_reading_the_chunks()
    {
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.InternalServerError, """{"error":"broken"}""");
        await using var provider = ClientServices.Build(handler, new FakeTimeProvider(DateTimeOffset.UnixEpoch));

        var failure = await Should.ThrowAsync<InngestException>(provider.GetRequiredService<IInngestRealtime>()
            .PublishStreamAsync(Tokens, Endless(), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        failure.Message.ShouldBe("Failed to publish to topic \"tokens\" of channel \"chat:42\": broken");
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        async IAsyncEnumerable<string> Endless([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                await foreach (var chunk in Full(1001))
                {
                    yield return chunk;
                }

                await Task.Delay(Timeout.Infinite, cancellationToken);
                yield return "never";
            }
            finally
            {
                stopped.SetResult();
            }
        }
    }

    // Past the pieces Inngest relays, the producer's exception comes from reading the rest of the text, not from the request.
    [Fact]
    public async Task What_the_producer_throws_after_the_pieces_Inngest_relays_reaches_the_caller_as_it_is()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK);
        await using var provider = ClientServices.Build(handler, new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        var refusal = new NonRetriableException("The model refused.");

        var thrown = await Should.ThrowAsync<NonRetriableException>(provider.GetRequiredService<IInngestRealtime>()
            .PublishStreamAsync(Tokens, FailingAfter(1001, refusal), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        thrown.ShouldBeSameAs(refusal);
        handler.Single.Body.Length.ShouldBe(1000 * 1024);
    }

    [Fact]
    public async Task A_stream_step_whose_producer_gives_up_after_the_pieces_Inngest_relays_fails_without_retries()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK);
        await using var client = ClientServices.Build(handler, new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        using var host = new EngineHost(services => services.AddSingleton(client.GetRequiredService<IRealtimeApi>()));
        var function = TestFunction.Define(async context =>
            await context.Step.PublishStreamAsync("reply", Tokens, FailingAfter(1001, new NonRetriableException("The model refused."))));

        var result = await host.CallAsync(function, Call.New());

        result.OpCodes.ShouldBe(["StepFailed"]);
        var error = result.Op().GetProperty("error");
        error.GetProperty("name").GetString().ShouldBe("NonRetriableException");
        error.GetProperty("message").GetString().ShouldBe("The model refused.");
        handler.Single.Body.Length.ShouldBe(1000 * 1024);
    }

    // Once the request has ended, the caller's token still stops the producer while the rest of the text is read.
    [Fact]
    public async Task Cancelling_a_publish_after_the_pieces_Inngest_relays_stops_the_producer()
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK);
        await using var provider = ClientServices.Build(handler, new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var publishing = provider.GetRequiredService<IInngestRealtime>().PublishStreamAsync(Tokens, Endless(), cancel.Token);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        handler.Single.Body.Length.ShouldBe(1000 * 1024);
        await cancel.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(publishing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        async IAsyncEnumerable<string> Endless([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                await foreach (var chunk in Full(1001))
                {
                    yield return chunk;
                }

                waiting.SetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                yield return "never";
            }
            finally
            {
                stopped.SetResult();
            }
        }
    }

    // The request's token belongs to the network stack, which may cancel it once it is done with the request.
    [Fact]
    public async Task The_requests_token_does_not_stop_the_producer_once_the_body_has_ended()
    {
        var more = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var content = new TextStreamContent(Long(), TimeProvider.System);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        await content.CopyToAsync(Stream.Null, request.Token);
        await request.CancelAsync();
        more.SetResult();
        var text = await content.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        content.Pieces.ShouldBe(1000);
        text.ShouldBe(new string('x', 1001 * 1024) + " and the rest");

        async IAsyncEnumerable<string> Long([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var chunk in Full(1001))
            {
                yield return chunk;
            }

            await more.Task.WaitAsync(cancellationToken);
            yield return " and the rest";
        }
    }

    [Fact]
    public async Task A_stream_within_what_Inngest_relays_logs_nothing()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK);
        await using var provider = ClientServices.Build(handler, new FakeTimeProvider(DateTimeOffset.UnixEpoch), configureServices: services => services.AddFakeLogging());

        await provider.GetRequiredService<IInngestRealtime>().PublishStreamAsync(Tokens, Full(1000), TestContext.Current.CancellationToken);

        provider.GetFakeLogCollector().GetSnapshot().ShouldNotContain(record => record.Id.Id == 354);
    }

    [Fact]
    public async Task A_stream_step_publishes_with_the_run_and_memoizes_the_text()
    {
        var api = new FakeRealtimeApi();
        using var host = new EngineHost(services => services.AddSingleton<IRealtimeApi>(api));
        var function = TestFunction.Define(async context => await context.Step.PublishStreamAsync("reply", Tokens, Pieces("Hello", " world")));

        var result = await host.CallAsync(function, Call.New());

        result.OpCodes.ShouldBe(["StepRun"]);
        result.Op().GetProperty("opts").GetRawText().ShouldBe("""{"type":"step.realtime.publish"}""");
        result.Op().GetProperty("data").GetString().ShouldBe("Hello world");
        api.Published.ShouldBe([(Tokens, (string?)"01JRUN", "Hello world")]);
    }

    // The producer, such as a model, failed and not the request, so its exception is the caller's to catch.
    [Fact]
    public async Task What_the_producer_throws_reaches_the_caller_as_it_is()
    {
        var handler = new FakeHttpHandler().RespondAlways(HttpStatusCode.OK);
        await using var provider = ClientServices.Build(handler, new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        var realtime = provider.GetRequiredService<IInngestRealtime>();

        var refused = await Should.ThrowAsync<NonRetriableException>(
            realtime.PublishStreamAsync(Tokens, Failing(new NonRetriableException("The model refused.")), TestContext.Current.CancellationToken));
        var overloaded = await Should.ThrowAsync<ModelException>(
            realtime.PublishStreamAsync(Tokens, Failing(new ModelException("The model is overloaded.")), TestContext.Current.CancellationToken));

        refused.Message.ShouldBe("The model refused.");
        overloaded.Message.ShouldBe("The model is overloaded.");
    }

    [Fact]
    public async Task A_stream_step_whose_producer_gives_up_for_good_fails_without_retries()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK);
        await using var client = ClientServices.Build(handler, new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        using var host = new EngineHost(services => services.AddSingleton(client.GetRequiredService<IRealtimeApi>()));
        var function = TestFunction.Define(async context =>
            await context.Step.PublishStreamAsync("reply", Tokens, Failing(new NonRetriableException("The model refused."))));

        var result = await host.CallAsync(function, Call.New());

        result.OpCodes.ShouldBe(["StepFailed"]);
        var error = result.Op().GetProperty("error");
        error.GetProperty("name").GetString().ShouldBe("NonRetriableException");
        error.GetProperty("message").GetString().ShouldBe("The model refused.");
    }

    [Fact]
    public async Task A_replayed_stream_step_does_not_read_its_chunks_again()
    {
        var api = new FakeRealtimeApi();
        var reads = 0;
        using var host = new EngineHost(services => services.AddSingleton<IRealtimeApi>(api));
        var function = TestFunction.Define(async context => await context.Step.PublishStreamAsync("reply", Tokens, Counted()));

        var result = await host.CallAsync(function, Call.New().Data("reply", "\"Hello world\""));

        result.Body.ShouldBe("\"Hello world\"");
        reads.ShouldBe(0);
        api.Published.ShouldBeEmpty();

        async IAsyncEnumerable<string> Counted()
        {
            reads++;
            yield return "never";
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_typed_topic_publishes_its_data_type_from_steps_and_from_anywhere()
    {
        var api = new FakeRealtimeApi();
        var status = new RealtimeTopic<Progress>("order:7", "status");
        using var host = new EngineHost(services => services.AddSingleton<IRealtimeApi>(api));
        var function = TestFunction.Define(async context => (await context.Step.PublishAsync("status", status, new Progress("Packed", 50))).Percent);

        var result = await host.CallAsync(function, Call.New());
        var realtime = new InngestRealtime(api, new RealtimeSubscriber(api, TimeProvider.System, NullLogger<RealtimeSubscriber>.Instance), EngineHost.Settings, NullLogger<InngestRealtime>.Instance);
        await realtime.PublishAsync(status, new Progress("Shipped", 100), TestContext.Current.CancellationToken);

        result.Op().GetProperty("data").GetRawText().ShouldBe("""{"text":"Packed","percent":50}""");
        api.Published.Select(publish => publish.Body).ShouldBe(["""{"text":"Packed","percent":50}""", """{"text":"Shipped","percent":100}"""]);
        api.Published.ShouldAllBe(publish => publish.Topic == status.Untyped);
    }

    // Chunks of 1 KB, each of which fills a piece.
    private static async IAsyncEnumerable<string> Full(int count)
    {
        for (var i = 0; i < count; i++)
        {
            await Task.Yield();
            yield return new string('x', 1024);
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        using var giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(5, giveUp.Token);
        }
    }

    private static async IAsyncEnumerable<string> Pieces(params string[] pieces)
    {
        foreach (var piece in pieces)
        {
            await Task.Yield();
            yield return piece;
        }
    }

    // Yields some text and then fails, as a model can in the middle of its reply.
    private static async IAsyncEnumerable<string> Failing(Exception exception)
    {
        await Task.Yield();
        yield return "Hello";
        throw exception;
    }

    // Yields count full chunks and then fails.
    private static async IAsyncEnumerable<string> FailingAfter(int count, Exception exception)
    {
        await foreach (var chunk in Full(count))
        {
            yield return chunk;
        }

        throw exception;
    }

    private sealed record Progress(string Text, int Percent);

    private sealed class ModelException(string message) : Exception(message);

    // Reads the body as the network stack sends it, until the stream ends or the request is cancelled.
    private sealed class Receiving : HttpMessageHandler
    {
        private readonly TaskCompletionSource _received = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? Body { get; private set; }

        // Completes once the body has ended.
        public Task Received => _received.Task;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = new MemoryStream();
            await request.Content!.CopyToAsync(body, cancellationToken);
            Body = Encoding.UTF8.GetString(body.ToArray());
            _received.TrySetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    // Keeps every write the content makes, to check where it cuts the text.
    private sealed class RecordingStream : MemoryStream
    {
        public List<byte[]> Pieces { get; } = [];

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Pieces.Add(buffer.ToArray());
            return base.WriteAsync(buffer, cancellationToken);
        }
    }
}
